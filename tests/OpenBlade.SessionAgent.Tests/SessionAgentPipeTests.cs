using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using OpenBlade.Contracts;
using OpenBlade.Session;
using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SessionAgentPipeTests
{
    [Fact]
    public void Endpoint_IsStableForSameUserAndContainsNoRawSid()
    {
        SecurityIdentifier sid = WindowsIdentity.GetCurrent().User!;

        SessionAgentPipeEndpoint first = SessionAgentPipeEndpoint.Create(sid, 3);
        SessionAgentPipeEndpoint second = SessionAgentPipeEndpoint.Create(sid, 3);

        Assert.Equal(first, second);
        Assert.StartsWith(SessionDisplayIpc.PipeNamePrefix + ".", first.PipeName);
        Assert.DoesNotContain(sid.Value, first.PipeName, StringComparison.Ordinal);
    }

    [Fact]
    public void Endpoint_IsIsolatedByWindowsSession()
    {
        SecurityIdentifier sid = WindowsIdentity.GetCurrent().User!;

        Assert.NotEqual(
            SessionAgentPipeEndpoint.Create(sid, 3),
            SessionAgentPipeEndpoint.Create(sid, 4));
    }

    [Fact]
    public void Security_IsProtectedAndAllowsOnlyCurrentUser()
    {
        SecurityIdentifier sid = WindowsIdentity.GetCurrent().User!;

        PipeSecurity security = SessionAgentPipeSecurity.Create(sid);
        AuthorizationRuleCollection rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            typeof(SecurityIdentifier));
        PipeAccessRule rule = Assert.Single(rules.Cast<PipeAccessRule>());

        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(sid, rule.IdentityReference);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(PipeAccessRights.FullControl, rule.PipeAccessRights);
    }

    [Fact]
    public async Task PipeOwnership_IsAtomicAndReleasedOnDispose()
    {
        SecurityIdentifier sid = WindowsIdentity.GetCurrent().User!;
        var endpoint = new SessionAgentPipeEndpoint(
            $"{SessionDisplayIpc.PipeNamePrefix}.Tests.{Guid.NewGuid():N}");

        Assert.True(SessionAgentPipeOwner.TryAcquire(endpoint, sid, out SessionAgentPipeOwner? first));
        Assert.False(SessionAgentPipeOwner.TryAcquire(endpoint, sid, out SessionAgentPipeOwner? duplicate));
        Assert.Null(duplicate);
        await first!.DisposeAsync();

        Assert.True(SessionAgentPipeOwner.TryAcquire(endpoint, sid, out SessionAgentPipeOwner? replacement));
        await replacement!.DisposeAsync();
    }

    [Fact]
    public async Task AcceptAsync_CanceledIOExceptionCompletesNormally()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        SessionAgentPipeAcceptResult result = await SessionAgentPipeAccept.AcceptAsync(
            _ => Task.FromException(new IOException("The pipe is being closed.")),
            cancellation.Token);

        Assert.Equal(SessionAgentPipeAcceptResult.Canceled, result);
    }

    [Fact]
    public async Task AcceptAsync_UncanceledPipeClosingRequestsReplacement()
    {
        const int pipeClosingHResult = unchecked((int)0x800700E8);

        SessionAgentPipeAcceptResult result = await SessionAgentPipeAccept.AcceptAsync(
            _ => Task.FromException(new IOException(
                "The pipe is being closed.",
                pipeClosingHResult)),
            CancellationToken.None);

        Assert.Equal(SessionAgentPipeAcceptResult.PipeClosed, result);
    }

    [Fact]
    public async Task WindowsAccept_ClientClosesBeforeWaitReportsPipeClosing()
    {
        const int pipeClosingHResult = unchecked((int)0x800700E8);
        SecurityIdentifier sid = WindowsIdentity.GetCurrent().User!;
        var endpoint = new SessionAgentPipeEndpoint(
            $"{SessionGpuIpc.PipeNamePrefix}.Tests.{Guid.NewGuid():N}");
        Assert.True(SessionAgentPipeOwner.TryAcquire(endpoint, sid, out SessionAgentPipeOwner? owner));
        await using SessionAgentPipeOwner acquiredOwner = owner!;
        await using NamedPipeServerStream server = acquiredOwner.TakeInitialPipe();
        await using (var client = new NamedPipeClientStream(
            ".",
            endpoint.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(timeout.Token);
        }

        IOException exception = await Assert.ThrowsAsync<IOException>(
            () => server.WaitForConnectionAsync(CancellationToken.None));

        Assert.Equal(pipeClosingHResult, exception.HResult);
    }

    [Fact]
    public async Task AcceptAsync_UnexpectedIOExceptionRemainsObservable()
    {
        var expected = new IOException("unexpected accept failure");

        IOException actual = await Assert.ThrowsAsync<IOException>(() =>
            SessionAgentPipeAccept.AcceptAsync(
                _ => Task.FromException(expected),
                CancellationToken.None));

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task DispatchFailureReportsBoundedMetadataAndKeepsCorrelatedFallback()
    {
        const int failureHResult = unchecked((int)0x80070005);
        SecurityIdentifier sid = WindowsIdentity.GetCurrent().User!;
        var endpoint = new SessionAgentPipeEndpoint(
            $"{SessionBatteryIpc.PipeNameSuffix}.DispatchFailure.{Guid.NewGuid():N}");
        Assert.True(SessionAgentPipeOwner.TryAcquire(endpoint, sid, out SessionAgentPipeOwner? owner));
        var failures = new List<DiagnosticFailure>();
        SessionBatteryResponse? written = null;
        var request = new SessionBatteryRequest(
            SessionBatteryIpc.ProtocolVersion,
            Guid.NewGuid());
        await using var server = new SessionAgentPipeServer<SessionBatteryRequest, SessionBatteryResponse>(
            owner!,
            (_, _) => Task.FromResult(request),
            (_, response, _) =>
            {
                written = response;
                return Task.CompletedTask;
            },
            (_, _) => Task.FromException<SessionBatteryResponse>(
                new IOException(
                    @"dispatch failed at C:\Users\Alice\battery.json",
                    failureHResult)),
            new SessionAgentPipeServerPolicy<SessionBatteryRequest, SessionBatteryResponse>(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                static _ => TimeSpan.FromSeconds(1),
                static (failedRequest, _) => new SessionBatteryResponse(
                    SessionBatteryIpc.ProtocolVersion,
                    failedRequest.RequestId,
                    SessionBatteryOperationStatus.Failed,
                    "The Session Agent could not read the Windows battery report.",
                    null)),
            timeProvider: null,
            diagnosticFailureSink: failures.Add);

        await server.ServeConnectionAsync(Stream.Null, CancellationToken.None);

        Assert.NotNull(written);
        Assert.Equal(request.RequestId, written.RequestId);
        Assert.Equal(SessionBatteryOperationStatus.Failed, written.Status);
        Assert.DoesNotContain("Alice", written.Error, StringComparison.Ordinal);
        DiagnosticFailure failure = Assert.Single(failures);
        Assert.True(failure.IsValid());
        Assert.Equal("session-agent.ipc", failure.Component);
        Assert.Equal(nameof(SessionBatteryRequest), failure.Operation);
        Assert.Equal("dispatch", failure.Stage);
        Assert.Equal("exception", failure.Reason);
        Assert.Equal(nameof(IOException), failure.ExceptionType);
        Assert.Equal(failureHResult, failure.HResult);
        Assert.Contains("dispatch failed", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Alice", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateStartupDoesNotRegisterPersistentLogging()
    {
        SecurityIdentifier sid = WindowsIdentity.GetCurrent().User!;
        var endpoint = new SessionAgentPipeEndpoint(
            $"{SessionDisplayIpc.PipeNamePrefix}.Tests.{Guid.NewGuid():N}");
        Assert.True(
            SessionAgentPipeOwner.TryAcquire(
                endpoint,
                sid,
                out SessionAgentPipeOwner? existing));
        int registrationCount = 0;

        bool acquired = Program.TryAcquireStartupOwnership(
            endpoint,
            sid,
            () =>
            {
                registrationCount++;
                return new CancellationTokenSource();
            },
            out SessionAgentPipeOwner? duplicate,
            out IDisposable? traceRegistration);

        Assert.False(acquired);
        Assert.Equal(0, registrationCount);
        Assert.Null(duplicate);
        Assert.Null(traceRegistration);
        await existing!.DisposeAsync();
    }

    [Fact]
    public async Task SuccessfulStartupRegistersLoggingAfterOwnership()
    {
        SecurityIdentifier sid = WindowsIdentity.GetCurrent().User!;
        var endpoint = new SessionAgentPipeEndpoint(
            $"{SessionDisplayIpc.PipeNamePrefix}.Tests.{Guid.NewGuid():N}");
        int registrationCount = 0;

        Assert.True(Program.TryAcquireStartupOwnership(
            endpoint,
            sid,
            () =>
            {
                registrationCount++;
                return new CancellationTokenSource();
            },
            out SessionAgentPipeOwner? owner,
            out IDisposable? traceRegistration));
        Assert.Equal(1, registrationCount);

        traceRegistration!.Dispose();
        await owner!.DisposeAsync();
    }

    [Fact]
    public async Task FailedLoggingRegistrationReleasesStartupOwnership()
    {
        SecurityIdentifier sid = WindowsIdentity.GetCurrent().User!;
        var endpoint = new SessionAgentPipeEndpoint(
            $"{SessionDisplayIpc.PipeNamePrefix}.Tests.{Guid.NewGuid():N}");

        Assert.Throws<InvalidOperationException>(() =>
            Program.TryAcquireStartupOwnership(
                endpoint,
                sid,
                static () => throw new InvalidOperationException("registration failed"),
                out _,
                out _));

        Assert.True(
            SessionAgentPipeOwner.TryAcquire(
                endpoint,
                sid,
                out SessionAgentPipeOwner? replacement));
        await replacement!.DisposeAsync();
    }
}

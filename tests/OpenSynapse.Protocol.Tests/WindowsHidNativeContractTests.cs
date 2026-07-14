using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OpenSynapse.Protocol;

namespace OpenSynapse.Protocol.Tests;

public sealed class WindowsHidNativeContractTests
{
    [Fact]
    public void NativeContract_HasExactConstantsInterfacesAndLibraryImports()
    {
        Type type = typeof(WindowsHidNative);

        Assert.Contains(typeof(IHidCandidateNative), type.GetInterfaces());
        Assert.Contains(typeof(IOverlappedFeatureNative), type.GetInterfaces());
        Assert.Equal(0x000B0191u, ReadConstant<uint>(type, "IoctlHidSetFeature"));
        Assert.Equal(0x000B0192u, ReadConstant<uint>(type, "IoctlHidGetFeature"));
        Assert.Equal(997, ReadConstant<int>(type, "ErrorIoPending"));
        Assert.Equal(1168, ReadConstant<int>(type, "ErrorNotFound"));

        AssertLibraryImport(type, "DeviceIoControl", "kernel32.dll", setLastError: true);
        AssertLibraryImport(type, "CancelIoEx", "kernel32.dll", setLastError: true);
        Assert.DoesNotContain(
            typeof(WindowsHidEnumerator).GetMethods(BindingFlags.Static | BindingFlags.NonPublic),
            method => method.GetCustomAttribute<LibraryImportAttribute>() is not null);
    }

    [Fact]
    public void OperationalOpenContract_RemainsOverlappedAndNonInheritable()
    {
        HidOpenParameters parameters = (HidOpenParameters)typeof(WindowsRazerHidSessionOpener)
            .GetField("OperationalOpenParameters", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;

        Assert.Equal(new HidOpenParameters(0xC0000000u, 3, 3, 0x40000000u, false), parameters);
    }

    [Fact]
    public void ProductionSources_KeepRealFeatureInteropEntirelyUnwired()
    {
        string sourceDirectory = Path.Combine(FindRepositoryRoot(), "src");
        string[] sourceFiles = Directory.GetFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories);
        var sources = sourceFiles.ToDictionary(path => path, File.ReadAllText);

        Assert.DoesNotContain(sources.Values, source => source.Contains("HidD_SetFeature"));
        Assert.DoesNotContain(sources.Values, source => source.Contains("HidD_GetFeature"));
        Assert.DoesNotContain(
            sources.Where(pair => !pair.Key.EndsWith("WindowsHidEnumerator.cs")),
            pair => pair.Value.Contains("new WindowsHidNative("));
        Assert.DoesNotContain(
            sources,
            pair => pair.Value.Contains("OverlappedFeatureOperation") &&
                pair.Value.Contains("WindowsHidNative") &&
                !pair.Key.EndsWith("WindowsHidNative.cs"));
    }

    [Theory]
    [InlineData(0x000B0191u, true)]
    [InlineData(0x000B0192u, false)]
    public void Submit_UsesExactBufferPointerAndDirection(uint controlCode, bool isSet)
    {
        var kernel = new ScriptedKernel();
        var native = new WindowsHidNative(kernel);
        byte[] buffer = new byte[91];
        using var token = new NativeOperationToken(buffer, _ => { });

        NativeSubmissionResult result = native.Submit(
            CreateHandle(), controlCode, buffer, token, _ => { });

        Assert.Equal(NativeSubmissionKind.Submitted, result.Kind);
        Assert.Same(buffer, kernel.PinData);
        Assert.Same(buffer, token.PinnedBuffer);
        KernelIoCall call = Assert.Single(kernel.IoCalls);
        Assert.Equal(kernel.Pointer, call.Overlapped);
        Assert.Equal(controlCode, call.ControlCode);
        if (isSet)
        {
            Assert.Equal(token.PinnedAddress, call.Input);
            Assert.Equal(91u, call.InputLength);
            Assert.Equal(IntPtr.Zero, call.Output);
            Assert.Equal(0u, call.OutputLength);
        }
        else
        {
            Assert.Equal(IntPtr.Zero, call.Input);
            Assert.Equal(0u, call.InputLength);
            Assert.Equal(token.PinnedAddress, call.Output);
            Assert.Equal(91u, call.OutputLength);
        }
    }

    [Theory]
    [InlineData(true, 0, (int)NativeSubmissionKind.Submitted, 0)]
    [InlineData(false, 997, (int)NativeSubmissionKind.Pending, 997)]
    [InlineData(false, 5, (int)NativeSubmissionKind.ImmediateFailure, 5)]
    public void Submit_MapsNativeResultWithoutManualCompletion(
        bool succeeded,
        int error,
        int expectedKind,
        int expectedError)
    {
        var kernel = new ScriptedKernel { IoResult = new NativeCallResult(succeeded, error) };
        var native = new WindowsHidNative(kernel);
        int completions = 0;
        using var token = new NativeOperationToken(new byte[91], _ => { });

        NativeSubmissionResult result = native.Submit(
            CreateHandle(), 0x000B0191u, token.PinnedBuffer, token, _ => completions++);

        Assert.Equal((NativeSubmissionKind)expectedKind, result.Kind);
        Assert.Equal(expectedError, result.ErrorCode);
        Assert.Equal(0, completions);
        Assert.Single(kernel.IoCalls);
    }

    [Fact]
    public void Submit_AllowsCompletionBeforeDeviceCallReturnsAndDoesNotDoubleCompleteOnTrue()
    {
        var kernel = new ScriptedKernel
        {
            IoResult = new NativeCallResult(true, 0),
            CompleteInsideIo = new NativeIoCompletion(false, 37, 123),
        };
        var native = new WindowsHidNative(kernel);
        var completions = new List<NativeIoCompletion>();
        using var token = new NativeOperationToken(new byte[91], _ => { });

        NativeSubmissionResult result = native.Submit(
            CreateHandle(), 0x000B0192u, token.PinnedBuffer, token, completions.Add);

        Assert.Equal(NativeSubmissionKind.Submitted, result.Kind);
        Assert.Equal([new NativeIoCompletion(false, 37, 123)], completions);
    }

    [Fact]
    public void Submit_RejectsUnknownControlCodeBeforeAllocationOrIo()
    {
        var kernel = new ScriptedKernel();
        var native = new WindowsHidNative(kernel);
        using var token = new NativeOperationToken(new byte[91], _ => { });

        NativeSubmissionResult result = native.Submit(
            CreateHandle(), 0xDEADBEEFu, token.PinnedBuffer, token, _ => { });

        Assert.Equal(NativeSubmissionKind.ImmediateFailure, result.Kind);
        Assert.Equal(87, result.ErrorCode);
        Assert.Equal(0, kernel.AllocateCalls);
        Assert.Empty(kernel.IoCalls);
    }

    [Theory]
    [InlineData(true, 0, (int)NativeCancelResult.Requested)]
    [InlineData(false, 1168, (int)NativeCancelResult.NotFound)]
    [InlineData(false, 5, (int)NativeCancelResult.Failed)]
    public void Cancel_UsesExactPointerAndMapsResultWithoutFreeOrCompletion(
        bool succeeded,
        int error,
        int expected)
    {
        var kernel = new ScriptedKernel { CancelResult = new NativeCallResult(succeeded, error) };
        var native = new WindowsHidNative(kernel);
        int completions = 0;
        using var token = new NativeOperationToken(new byte[91], _ => { });
        _ = native.Submit(
            CreateHandle(), 0x000B0191u, token.PinnedBuffer, token, _ => completions++);

        NativeCancelResult result = native.Cancel(CreateHandle(), token);

        Assert.Equal((NativeCancelResult)expected, result);
        Assert.Equal(kernel.Pointer, Assert.Single(kernel.CancelPointers));
        Assert.Equal(0, kernel.FreeCalls);
        Assert.Equal(0, completions);
    }

    [Fact]
    public void ManagedFailures_ReturnStableResultsAndOnlyUnattachedAllocationIsFreed()
    {
        var allocationFailure = new ScriptedKernel
        {
            AllocateException = new ObjectDisposedException("bound"),
        };
        var allocationNative = new WindowsHidNative(allocationFailure);
        using var first = new NativeOperationToken(new byte[91], _ => { });

        NativeSubmissionResult allocationResult = allocationNative.Submit(
            CreateHandle(), 0x000B0191u, first.PinnedBuffer, first, _ => { });

        Assert.Equal(NativeSubmissionKind.ImmediateFailure, allocationResult.Kind);
        Assert.Equal(new ObjectDisposedException("bound").HResult, allocationResult.ErrorCode);
        Assert.Equal(0, allocationFailure.FreeCalls);

        var attachFailure = new ScriptedKernel();
        var attachNative = new WindowsHidNative(attachFailure);
        using var second = new NativeOperationToken(new byte[91], _ => { });
        var existingOwner = new ScriptedKernel();
        second.Attach(CreateHandle().BoundHandle, existingOwner, existingOwner.Pointer);

        NativeSubmissionResult attachResult = attachNative.Submit(
            CreateHandle(), 0x000B0191u, second.PinnedBuffer, second, _ => { });

        Assert.Equal(NativeSubmissionKind.ImmediateFailure, attachResult.Kind);
        Assert.Equal(new InvalidOperationException().HResult, attachResult.ErrorCode);
        Assert.Equal(1, attachFailure.FreeCalls);
        Assert.Empty(attachFailure.IoCalls);
    }

    [Fact]
    public void SubmitException_ReturnsStableFailureAndLeavesAttachedAllocationForTokenOwner()
    {
        var expected = new InvalidOperationException("submit");
        var kernel = new ScriptedKernel { IoException = expected };
        var native = new WindowsHidNative(kernel);
        var token = new NativeOperationToken(new byte[91], _ => { });

        NativeSubmissionResult result = native.Submit(
            CreateHandle(), 0x000B0191u, token.PinnedBuffer, token, _ => { });

        Assert.Equal(NativeSubmissionKind.ImmediateFailure, result.Kind);
        Assert.Equal(expected.HResult, result.ErrorCode);
        Assert.Equal(0, kernel.FreeCalls);
        token.Dispose();
        Assert.Equal(1, kernel.FreeCalls);
    }

    [Fact]
    public void CancelException_ReturnsFailedWithoutFreeOrCompletion()
    {
        var kernel = new ScriptedKernel
        {
            CancelException = new ObjectDisposedException("handle"),
        };
        var native = new WindowsHidNative(kernel);
        int completions = 0;
        using var token = new NativeOperationToken(new byte[91], _ => { });
        _ = native.Submit(
            CreateHandle(), 0x000B0191u, token.PinnedBuffer, token, _ => completions++);

        NativeCancelResult result = native.Cancel(CreateHandle(), token);

        Assert.Equal(NativeCancelResult.Failed, result);
        Assert.Equal(0, kernel.FreeCalls);
        Assert.Equal(0, completions);
    }

    [Fact]
    public async Task CompletionPreservesValuesAndFreesExactlyOnceAfterOperationFinalization()
    {
        var kernel = new ScriptedKernel { IoResult = new NativeCallResult(false, 997) };
        var native = new WindowsHidNative(kernel);
        byte[] buffer = new byte[91];

        Task<HidFeatureIoResult> execution = new OverlappedFeatureOperation(native).ExecuteAsync(
            CreateHandle(), 0x000B0192u, buffer, CancellationToken.None, CancellationToken.None);

        Assert.False(execution.IsCompleted);
        Assert.Equal(0, kernel.FreeCalls);
        kernel.Complete(new NativeIoCompletion(false, 42, 995));
        HidFeatureIoResult result = await execution;

        Assert.Equal(new NativeIoCompletion(false, 42, 995), result.Completion);
        Assert.Equal(1, kernel.FreeCalls);
    }

    [Fact]
    public void TokenDispose_ReleasesPinEvenWhenNativeFreeThrows()
    {
        var kernel = new ScriptedKernel { FreeException = new InvalidOperationException("free") };
        var token = new NativeOperationToken(new byte[91], _ => { });
        token.Attach(CreateHandle().BoundHandle, kernel, kernel.Pointer);

        Assert.Throws<InvalidOperationException>(token.Dispose);

        Assert.False(token.IsPinAllocated);
        Assert.True(token.IsOverlappedReleased);
        Assert.Equal(1, kernel.FreeCalls);
        token.Dispose();
        Assert.Equal(1, kernel.FreeCalls);
    }

    private static T ReadConstant<T>(Type type, string name) =>
        (T)type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null &&
            !File.Exists(Path.Combine(directory.FullName, "OpenSynapse.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
            throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static void AssertLibraryImport(Type type, string name, string library, bool setLastError)
    {
        MethodInfo method = type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
        LibraryImportAttribute import = method.GetCustomAttribute<LibraryImportAttribute>()!;
        Assert.Equal(library, import.LibraryName);
        Assert.Equal(setLastError, import.SetLastError);
    }

    private static OpenedHidHandle CreateHandle()
    {
        var file = new SafeFileHandle(new IntPtr(123), ownsHandle: false);
        var bound = (ThreadPoolBoundHandle)RuntimeHelpers.GetUninitializedObject(
            typeof(ThreadPoolBoundHandle));
        return new OpenedHidHandle(file, bound, () => { }, () => file.Dispose());
    }

    private readonly record struct KernelIoCall(
        uint ControlCode,
        IntPtr Input,
        uint InputLength,
        IntPtr Output,
        uint OutputLength,
        IntPtr Overlapped);

    private sealed class ScriptedKernel : IWindowsHidKernel
    {
        private NativeCompletionContext? completion;

        public IntPtr Pointer { get; } = new(456);
        public byte[]? PinData { get; private set; }
        public int AllocateCalls { get; private set; }
        public int FreeCalls { get; private set; }
        public Exception? AllocateException { get; init; }
        public Exception? FreeException { get; init; }
        public Exception? IoException { get; init; }
        public Exception? CancelException { get; init; }
        public NativeCallResult IoResult { get; init; } = new(true, 0);
        public NativeCallResult CancelResult { get; init; } = new(true, 0);
        public NativeIoCompletion? CompleteInsideIo { get; init; }
        public List<KernelIoCall> IoCalls { get; } = [];
        public List<IntPtr> CancelPointers { get; } = [];

        public IntPtr AllocateNativeOverlapped(
            ThreadPoolBoundHandle boundHandle,
            NativeCompletionContext completionContext,
            byte[] pinData)
        {
            AllocateCalls++;
            if (AllocateException is not null)
            {
                throw AllocateException;
            }
            completion = completionContext;
            PinData = pinData;
            return Pointer;
        }

        public void FreeNativeOverlapped(ThreadPoolBoundHandle boundHandle, IntPtr overlapped)
        {
            FreeCalls++;
            Assert.Equal(Pointer, overlapped);
            if (FreeException is not null)
            {
                throw FreeException;
            }
        }

        public NativeCallResult DeviceIoControl(
            SafeFileHandle fileHandle,
            uint controlCode,
            IntPtr input,
            uint inputLength,
            IntPtr output,
            uint outputLength,
            IntPtr overlapped)
        {
            IoCalls.Add(new KernelIoCall(
                controlCode, input, inputLength, output, outputLength, overlapped));
            if (IoException is not null)
            {
                throw IoException;
            }
            if (CompleteInsideIo is { } value)
            {
                Complete(value);
            }
            return IoResult;
        }

        public NativeCallResult CancelIoEx(SafeFileHandle fileHandle, IntPtr overlapped)
        {
            CancelPointers.Add(overlapped);
            if (CancelException is not null)
            {
                throw CancelException;
            }
            return CancelResult;
        }

        public void Complete(NativeIoCompletion value) =>
            completion!.Complete((uint)value.ErrorCode, (uint)value.BytesTransferred);
    }
}

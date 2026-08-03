using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

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
        Assert.Equal(122, ReadConstant<int>(type, "ErrorInsufficientBuffer"));
        Assert.Equal(259, ReadConstant<int>(type, "ErrorNoMoreItems"));

        Assert.DoesNotContain(
            typeof(WindowsHidEnumerator).GetMethods(BindingFlags.Static | BindingFlags.NonPublic),
            method => method.GetCustomAttribute<LibraryImportAttribute>() is not null);
    }

    [Fact]
    public void DeviceIoControlImport_HasExactUnsafeWindowsSignature()
    {
        Type overlappedPointer = typeof(NativeOverlapped).MakePointerType();

        MethodInfo method = AssertLibraryImport(
            typeof(WindowsHidNative),
            "DeviceIoControl",
            "kernel32.dll",
            "DeviceIoControl",
            typeof(SafeFileHandle),
            typeof(uint),
            typeof(IntPtr),
            typeof(uint),
            typeof(IntPtr),
            typeof(uint),
            typeof(IntPtr),
            overlappedPointer);

        ParameterInfo overlapped = Assert.Single(
            method.GetParameters(),
            parameter => parameter.Name == "overlapped");
        Assert.True(overlapped.ParameterType.IsPointer);
        Assert.Equal(typeof(NativeOverlapped), overlapped.ParameterType.GetElementType());
    }

    [Fact]
    public void CancelIoExImport_HasExactUnsafeWindowsSignature()
    {
        Type overlappedPointer = typeof(NativeOverlapped).MakePointerType();

        MethodInfo method = AssertLibraryImport(
            typeof(WindowsHidNative),
            "CancelIoEx",
            "kernel32.dll",
            "CancelIoEx",
            typeof(SafeFileHandle),
            overlappedPointer);

        ParameterInfo overlapped = method.GetParameters()[1];
        Assert.True(overlapped.ParameterType.IsPointer);
        Assert.Equal(typeof(NativeOverlapped), overlapped.ParameterType.GetElementType());
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
    public void Sources_WireRealFeatureInteropOnlyThroughApprovedCompositionPoints()
    {
        string sourceDirectory = Path.Combine(FindRepositoryRoot(), "src");
        string separator = Path.DirectorySeparatorChar.ToString();
        string[] sourceFiles = Directory
            .GetFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains(
                    $"{separator}bin{separator}",
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var sources = sourceFiles.ToDictionary(path => path, File.ReadAllText);

        Assert.DoesNotContain(sources.Values, source => source.Contains("HidD_SetFeature"));
        Assert.DoesNotContain(sources.Values, source => source.Contains("HidD_GetFeature"));
        Assert.DoesNotContain(
            sources.Where(pair =>
                !pair.Key.EndsWith("WindowsHidEnumerator.cs")
                && !pair.Key.EndsWith("BatteryValidationFactory.cs")
                && !pair.Key.EndsWith("ProductionBatteryFactory.cs")
                && !pair.Key.EndsWith("ProductionLightingFactory.cs")
                && !pair.Key.EndsWith("GetterCandidateProbeRunner.cs")
                && !pair.Key.EndsWith("HidInputAccessProbe.cs")),
            pair => pair.Value.Contains("new WindowsHidNative("));
        string[] composition = sources
            .Where(pair => pair.Value.Contains("OverlappedFeatureOperation") &&
                pair.Value.Contains("WindowsHidNative") &&
                !pair.Key.EndsWith("WindowsHidNative.cs"))
            .Select(pair => Path.GetFileName(pair.Key))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [
                "BatteryValidationFactory.cs",
                "GetterCandidateProbeRunner.cs",
                "ProductionBatteryFactory.cs",
                "ProductionLightingFactory.cs",
            ],
            composition);
        string candidateProbePath = Assert.Single(
            sources.Keys,
            path => path.EndsWith("GetterCandidateProbeRunner.cs"));
        Assert.Contains(
            $"{separator}OpenBlade.Capture{separator}",
            candidateProbePath,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnumerationSource_FailsClosedOnNonTerminalSetupApiErrors()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "OpenBlade.Protocol",
            "WindowsHidNative.cs"));

        Assert.Contains("if (error == ErrorNoMoreItems)", source, StringComparison.Ordinal);
        Assert.Contains("throw new Win32Exception(error);", source, StringComparison.Ordinal);
        Assert.Contains("sizeError != ErrorInsufficientBuffer", source, StringComparison.Ordinal);
        Assert.Contains("requiredSize < minimumSize", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionLightingFactory_WiresMatrixIoAtExactFeatureLength()
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "OpenBlade.Protocol",
            "ProductionLightingFactory.cs");
        string lightingComposition = File.ReadAllText(path).Replace("\r\n", "\n");

        Assert.Contains(
            "new OverlappedFeatureOperation(\n" +
            "            native,\n" +
            "            TargetBladeDevice.MatrixFeatureReportByteLength)",
            lightingComposition);
        Assert.Contains(
            "new WindowsPid02E0MatrixFeatureWriter(\n" +
            "                            handle,\n" +
            "                            matrixIo,",
            lightingComposition);
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
    public async Task CompletionPreservesSuccessfulUnnumberedNinetyByteCount()
    {
        var kernel = new ScriptedKernel { IoResult = new NativeCallResult(false, 997) };
        var native = new WindowsHidNative(kernel);
        byte[] buffer = new byte[91];

        Task<HidFeatureIoResult> execution = new OverlappedFeatureOperation(native).ExecuteAsync(
            CreateHandle(), 0x000B0192u, buffer, CancellationToken.None, CancellationToken.None);

        kernel.Complete(new NativeIoCompletion(true, 90, 0));
        HidFeatureIoResult result = await execution;

        Assert.Equal(new NativeIoCompletion(true, 90, 0), result.Completion);
        Assert.True(result.WasSubmitted);
        Assert.Equal(HidFeatureTermination.Completed, result.Termination);
        Assert.Equal(1, kernel.FreeCalls);
    }

    [Fact]
    public async Task CompletionWaitsForFinalNativeUseLeaseBeforeFreeingExactOverlapped()
    {
        var kernel = new ScriptedKernel { IoResult = new NativeCallResult(false, 997) };
        var native = new WindowsHidNative(kernel);
        byte[] buffer = new byte[91];
        var token = new NativeOperationToken(buffer, _ => { });
        var state = new NativeOperationState(token.Dispose);

        Assert.True(state.TryBeginSubmission(out NativeUseLease submissionLease));
        NativeSubmissionResult submission = native.Submit(
            CreateHandle(),
            0x000B0192u,
            buffer,
            token,
            state.RecordCompletion);
        Assert.Equal(NativeSubmissionKind.Pending, submission.Kind);
        state.RecordSubmitted();
        state.RecordCancellationIntent(HidFeatureTermination.CallerCanceled);
        Assert.True(state.TryAcquireCancellationLease(out NativeUseLease finalLease));

        kernel.Complete(new NativeIoCompletion(true, 91, 0));
        submissionLease.Dispose();

        Assert.False(state.Completion.IsCompleted);
        Assert.Equal(0, kernel.FreeCalls);
        Assert.False(token.IsDisposed);

        finalLease.Dispose();
        HidFeatureIoResult result = await state.Completion;

        Assert.Equal(new NativeIoCompletion(true, 91, 0), result.Completion);
        Assert.Equal(HidFeatureTermination.CallerCanceled, result.Termination);
        Assert.Equal(1, kernel.FreeCalls);
        Assert.True(token.IsDisposed);
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
            !File.Exists(Path.Combine(directory.FullName, "OpenBlade.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
            throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static MethodInfo AssertLibraryImport(
        Type type,
        string name,
        string library,
        string entryPoint,
        params Type[] parameterTypes)
    {
        MethodInfo method = type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
        LibraryImportAttribute import = method.GetCustomAttribute<LibraryImportAttribute>()!;
        Assert.Equal(library, import.LibraryName);
        Assert.True(import.SetLastError);
        Assert.Equal(entryPoint, import.EntryPoint ?? method.Name);
        Assert.Equal(typeof(bool), method.ReturnType);
        MarshalAsAttribute returnMarshalling =
            method.ReturnParameter.GetCustomAttribute<MarshalAsAttribute>()!;
        Assert.Equal(UnmanagedType.Bool, returnMarshalling.Value);
        Assert.Equal(parameterTypes, method.GetParameters().Select(parameter => parameter.ParameterType));
        return method;
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

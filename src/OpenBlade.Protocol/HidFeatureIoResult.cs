namespace OpenBlade.Protocol;

internal enum HidFeatureTermination
{
    Completed,
    CallerCanceled,
    DeadlineExpired,
}

internal readonly record struct HidFeatureIoResult(
    NativeIoCompletion? Completion,
    bool WasSubmitted,
    HidFeatureTermination Termination,
    NativeIoTiming? Timing = null);

internal readonly record struct NativeIoTiming(
    TimeSpan SubmitToCompletion,
    TimeSpan CompletionToManagedReturn);

internal readonly record struct NativeIoCompletion(
    bool Succeeded,
    int BytesTransferred,
    int ErrorCode);

namespace OpenSynapse.Protocol;

internal enum HidFeatureTermination
{
    Completed,
    CallerCanceled,
    DeadlineExpired,
}

internal readonly record struct HidFeatureIoResult(
    NativeIoCompletion? Completion,
    bool WasSubmitted,
    HidFeatureTermination Termination);

internal readonly record struct NativeIoCompletion(
    bool Succeeded,
    int BytesTransferred,
    int ErrorCode);

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OpenBlade.Session;

namespace OpenBlade.Tray;

public sealed partial class MainWindow
{
    private static readonly TimeSpan ThxEnhancementCommitDelay = TimeSpan.FromMilliseconds(400);
    private readonly DispatcherQueueTimer thxNormalizationCommitTimer;
    private readonly DispatcherQueueTimer thxVoiceClarityCommitTimer;
    private readonly CoalescingCommitState<ThxEnhancementCommitRequest> thxNormalizationCommits = new();
    private readonly CoalescingCommitState<ThxEnhancementCommitRequest> thxVoiceClarityCommits = new();
    private bool thxNormalizationPointerInteraction;
    private bool thxVoiceClarityPointerInteraction;

    private void OnThxEnhancementPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (TryGetThxEnhancement(sender, out _, out ThxAudioEnhancementKind kind))
        {
            if (kind == ThxAudioEnhancementKind.SoundNormalization)
            {
                thxNormalizationPointerInteraction = true;
            }
            else
            {
                thxVoiceClarityPointerInteraction = true;
            }
        }
    }

    private void OnThxEnhancementPointerCompleted(object sender, PointerRoutedEventArgs args)
    {
        if (!TryGetThxEnhancement(sender, out Slider slider, out ThxAudioEnhancementKind kind))
        {
            return;
        }
        bool interacting = kind == ThxAudioEnhancementKind.SoundNormalization
            ? thxNormalizationPointerInteraction : thxVoiceClarityPointerInteraction;
        if (kind == ThxAudioEnhancementKind.SoundNormalization)
        {
            thxNormalizationPointerInteraction = false;
        }
        else
        {
            thxVoiceClarityPointerInteraction = false;
        }
        if (interacting)
        {
            ScheduleThxEnhancementCommit(slider, kind);
        }
    }

    private void OnThxEnhancementKeyUp(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right
            or Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down
            or Windows.System.VirtualKey.PageUp or Windows.System.VirtualKey.PageDown
            or Windows.System.VirtualKey.Home or Windows.System.VirtualKey.End
            && TryGetThxEnhancement(sender, out Slider slider, out ThxAudioEnhancementKind kind))
        {
            ScheduleThxEnhancementCommit(slider, kind);
        }
    }

    private void OnThxEnhancementLostFocus(object sender, RoutedEventArgs args)
    {
        if (TryGetThxEnhancement(sender, out Slider slider, out ThxAudioEnhancementKind kind))
        {
            ScheduleThxEnhancementCommit(slider, kind);
        }
    }

    private void OnThxEnhancementPointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        if (TryGetThxEnhancement(sender, out Slider slider, out ThxAudioEnhancementKind kind))
        {
            // Observe the value after the native Slider processes this input.
            dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low,
                () => ScheduleThxEnhancementCommit(slider, kind));
        }
    }

    private bool TryGetThxEnhancement(object sender, out Slider slider, out ThxAudioEnhancementKind kind)
    {
        slider = sender as Slider ?? null!;
        kind = default;
        return Volatile.Read(ref disposed) == 0 && slider is not null
            && Enum.TryParse(slider.Tag as string, out kind)
            && kind is ThxAudioEnhancementKind.SoundNormalization or ThxAudioEnhancementKind.VoiceClarity;
    }

    private void ScheduleThxEnhancementCommit(Slider slider, ThxAudioEnhancementKind kind)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }
        ThxEnhancementCommitRequest? request = ThxViewModel.CaptureEnhancementCommit(kind, slider.Value);
        if (request is null)
        {
            return;
        }
        CoalescingCommitState<ThxEnhancementCommitRequest> commits = kind == ThxAudioEnhancementKind.SoundNormalization
            ? thxNormalizationCommits : thxVoiceClarityCommits;
        DispatcherQueueTimer timer = kind == ThxAudioEnhancementKind.SoundNormalization
            ? thxNormalizationCommitTimer : thxVoiceClarityCommitTimer;
        commits.Schedule(request);
        timer.Stop();
        timer.Start();
    }

    private async void OnThxEnhancementCommitTimerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }
        CoalescingCommitState<ThxEnhancementCommitRequest> commits = ReferenceEquals(sender, thxNormalizationCommitTimer)
            ? thxNormalizationCommits : thxVoiceClarityCommits;
        CoalescingCommitBeginStatus status = commits.TryBegin(out ThxEnhancementCommitRequest? request);
        if (status == CoalescingCommitBeginStatus.InProgress)
        {
            sender.Start();
            return;
        }
        if (status != CoalescingCommitBeginStatus.Started)
        {
            return;
        }
        try
        {
            if (request is not null)
            {
                await RunHandlerAsync(() => ThxViewModel.CommitEnhancementAsync(request), nameof(OnThxEnhancementCommitTimerTick));
            }
        }
        finally
        {
            if (commits.Complete() && Volatile.Read(ref disposed) == 0)
            {
                sender.Start();
            }
        }
    }

    private void DisposeThxEnhancementCommits()
    {
        foreach (DispatcherQueueTimer timer in new[] { thxNormalizationCommitTimer, thxVoiceClarityCommitTimer })
        {
            timer.Stop();
            timer.Tick -= OnThxEnhancementCommitTimerTick;
        }
        thxNormalizationCommits.Dispose();
        thxVoiceClarityCommits.Dispose();
    }
}

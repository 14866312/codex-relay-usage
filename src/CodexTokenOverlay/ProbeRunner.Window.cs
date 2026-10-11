using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace CodexTokenOverlay;

// Window-level probes: edit gestures, capture loss, highlight regions, and theme
// binding. Split from ProbeRunner so the form and native probe code stays navigable.
internal static partial class ProbeRunner
{
    private static OverlayEditPreviewEventArgs ExecuteResizeProbe(
        TokenStripForm form,
        OverlayLayoutResult collapsedLayout,
        int delta)
    {
        form.ApplyLayout(collapsedLayout);
        OverlayEditPreviewEventArgs? preview = null;
        form.EditPreviewChanged += CapturePreview;
        var resizeHandle = form.EditResizeHandleBounds;
        var start = form.PointToScreen(new Point(
            resizeHandle.Left + Math.Max(0, resizeHandle.Width / 2),
            resizeHandle.Top + Math.Max(0, resizeHandle.Height / 2)));
        var current = new Point(start.X + delta, start.Y + delta);
        form.SimulateEditResize(start, current);
        form.EditPreviewChanged -= CapturePreview;
        form.SimulateEditGestureCompleted(current);
        return preview ?? throw new InvalidOperationException("缩放模拟未产生预览事件。");

        void CapturePreview(object? sender, OverlayEditPreviewEventArgs eventArgs) => preview = eventArgs;
    }

    private static EditCaptureLossProbeResult ExecuteLostCaptureProbe(
        OverlayLayoutResult collapsedLayout,
        OverlayEditGestureKind kind)
    {
        using var form = CreateEditingForm(collapsedLayout);
        OverlayEditPreviewEventArgs? preview = null;
        OverlayEditPreviewEventArgs? completed = null;
        var completionCount = 0;
        form.EditPreviewChanged += (_, eventArgs) => preview = eventArgs;
        form.EditGestureCompleted += (_, eventArgs) =>
        {
            completionCount++;
            completed = eventArgs;
        };

        var start = kind == OverlayEditGestureKind.Move
            ? ScreenCenter(collapsedLayout.WindowBounds, collapsedLayout.CapsuleBounds)
            : ResizeHandleCenter(form);
        var current = new Point(start.X + 31, start.Y + 19);
        if (kind == OverlayEditGestureKind.Move)
        {
            form.SimulateEditDrag(start, current);
        }
        else
        {
            form.SimulateEditResize(start, current);
        }

        var activeBeforeLoss = form.IsEditGestureActive;
        form.SimulateEditCaptureLost();
        var activeAfterLoss = form.IsEditGestureActive;
        var captureAfterLoss = form.Capture;
        form.SimulateEditCaptureLost();
        form.SimulateEditGestureCompleted(new Point(current.X + 5, current.Y + 5));
        var activeAfterRepeatedSignals = form.IsEditGestureActive;

        return new EditCaptureLossProbeResult(
            kind,
            preview,
            completed,
            completionCount,
            activeBeforeLoss,
            activeAfterLoss,
            activeAfterRepeatedSignals,
            captureAfterLoss,
            0);
    }

    private static EditCaptureLossProbeResult ExecuteCancelledCaptureProbe(
        OverlayLayoutResult collapsedLayout)
    {
        using var form = CreateEditingForm(collapsedLayout);
        OverlayEditPreviewEventArgs? preview = null;
        OverlayEditPreviewEventArgs? completed = null;
        var completionCount = 0;
        var cancelCount = 0;
        form.EditPreviewChanged += (_, eventArgs) => preview = eventArgs;
        form.EditGestureCompleted += (_, eventArgs) =>
        {
            completionCount++;
            completed = eventArgs;
        };
        form.EditCancelRequested += (_, _) => cancelCount++;

        var start = ScreenCenter(collapsedLayout.WindowBounds, collapsedLayout.CapsuleBounds);
        var current = new Point(start.X + 17, start.Y + 11);
        form.SimulateEditDrag(start, current);
        var activeBeforeCancel = form.IsEditGestureActive;
        form.SimulateEditCommand(Keys.Escape);
        form.EndEditMode();

        return new EditCaptureLossProbeResult(
            OverlayEditGestureKind.Move,
            preview,
            completed,
            completionCount,
            activeBeforeCancel,
            form.IsEditGestureActive,
            form.IsEditGestureActive,
            form.Capture,
            cancelCount);
    }

    private static EditCaptureLossProbeResult ExecuteDisposedCaptureProbe(
        OverlayLayoutResult collapsedLayout)
    {
        var form = CreateEditingForm(collapsedLayout);
        OverlayEditPreviewEventArgs? preview = null;
        OverlayEditPreviewEventArgs? completed = null;
        var completionCount = 0;
        form.EditPreviewChanged += (_, eventArgs) => preview = eventArgs;
        form.EditGestureCompleted += (_, eventArgs) =>
        {
            completionCount++;
            completed = eventArgs;
        };

        var start = ResizeHandleCenter(form);
        var current = new Point(start.X + 13, start.Y + 13);
        form.SimulateEditResize(start, current);
        var activeBeforeDispose = form.IsEditGestureActive;
        form.Dispose();

        return new EditCaptureLossProbeResult(
            OverlayEditGestureKind.Resize,
            preview,
            completed,
            completionCount,
            activeBeforeDispose,
            form.IsEditGestureActive,
            form.IsEditGestureActive,
            form.Capture,
            0);
    }

    private static TokenStripForm CreateEditingForm(OverlayLayoutResult collapsedLayout)
    {
        var form = new TokenStripForm();
        _ = form.Handle;
        form.ApplyLayout(collapsedLayout);
        form.BeginEditMode(collapsedLayout.ScalePercent);
        return form;
    }

    private static Point ResizeHandleCenter(TokenStripForm form)
    {
        var resizeHandle = form.EditResizeHandleBounds;
        return form.PointToScreen(new Point(
            resizeHandle.Left + Math.Max(0, resizeHandle.Width / 2),
            resizeHandle.Top + Math.Max(0, resizeHandle.Height / 2)));
    }

    private static bool HighlightHasRingRegion(
        AttachmentTargetHighlightForm form,
        int expectedThicknessPixels)
    {
        if (form.Region is null || form.ClientSize.Width < 8 || form.ClientSize.Height < 8)
        {
            return false;
        }

        var outer = new Rectangle(Point.Empty, form.ClientSize);
        using var expected = new Region(outer);
        if (form.ClientSize.Width > expectedThicknessPixels * 2
            && form.ClientSize.Height > expectedThicknessPixels * 2)
        {
            expected.Exclude(Rectangle.Inflate(
                outer,
                -expectedThicknessPixels,
                -expectedThicknessPixels));
        }

        using var graphics = form.CreateGraphics();
        return form.Region.Equals(expected, graphics);
    }

    private static object ExecuteThemeProbe()
    {
        var backgroundErrors = new List<string>();
        object? registryValue = 0;
        UserPreferenceChangedEventHandler? registeredHandler = null;
        UserPreferenceChangedEventHandler? capturedHandler = null;
        var subscribeCount = 0;
        var unsubscribeCount = 0;
        var sourceChangedCount = 0;
        var sourceChangedThread = -1;

        var source = new WindowsOverlayThemeSource(
            () => registryValue,
            handler =>
            {
                subscribeCount++;
                registeredHandler = handler;
                capturedHandler = handler;
            },
            handler =>
            {
                unsubscribeCount++;
                if (ReferenceEquals(registeredHandler, handler))
                {
                    registeredHandler = null;
                }
            });
        source.Changed += (_, _) =>
        {
            sourceChangedCount++;
            sourceChangedThread = Environment.CurrentManagedThreadId;
        };
        var sourceInitialDark = source.Current == OverlayThemeKind.Dark
            && subscribeCount == 1;
        registryValue = 1;
        var sourceChangeTask = RunCaptured(
            () => capturedHandler?.Invoke(
                source,
                new UserPreferenceChangedEventArgs(UserPreferenceCategory.General)),
            backgroundErrors);
        var sourceChangeCompleted = PumpUntil(() => sourceChangeTask.IsCompleted);
        var sourceChangedOnce = sourceChangeCompleted
            && source.Current == OverlayThemeKind.Light
            && sourceChangedCount == 1
            && sourceChangedThread != Environment.CurrentManagedThreadId;

        registryValue = 2;
        var sameSourceTask = RunCaptured(
            () => capturedHandler?.Invoke(
                source,
                new UserPreferenceChangedEventArgs(UserPreferenceCategory.General)),
            backgroundErrors);
        var sameSourceCompleted = PumpUntil(() => sameSourceTask.IsCompleted);
        var sourceSameKindIgnored = sameSourceCompleted && sourceChangedCount == 1;

        source.Dispose();
        source.Dispose();
        registryValue = 0;
        var postDisposeSourceTask = RunCaptured(
            () => capturedHandler?.Invoke(
                source,
                new UserPreferenceChangedEventArgs(UserPreferenceCategory.General)),
            backgroundErrors);
        var postDisposeSourceCompleted = PumpUntil(() => postDisposeSourceTask.IsCompleted);
        var sourcePostDisposeIgnored = postDisposeSourceCompleted
            && sourceChangedCount == 1
            && source.Current == OverlayThemeKind.Light;

        using var dispatcher = new Control();
        _ = dispatcher.Handle;
        var uiThreadId = Environment.CurrentManagedThreadId;
        var applied = new List<(Color Background, int ThreadId)>();
        var fake = new ProbeOverlayThemeSource(OverlayThemeKind.Dark);
        var binding = new OverlayThemeBinding(
            dispatcher,
            fake,
            palette => applied.Add((palette.Background, Environment.CurrentManagedThreadId)));
        var bindingInitialDark = applied.Count == 1
            && applied[0].Background == OverlayThemePalette.For(OverlayThemeKind.Dark).Background
            && applied[0].ThreadId == uiThreadId;

        var lightTask = RunCaptured(
            () => fake.Set(OverlayThemeKind.Light, forceEvent: true),
            backgroundErrors);
        var lightTaskCompleted = PumpUntil(() => lightTask.IsCompleted);
        var lightApplied = PumpUntil(() => applied.Count == 2);
        var bindingBackgroundLightOnUiThread = lightTaskCompleted
            && lightApplied
            && applied[1].Background == OverlayThemePalette.For(OverlayThemeKind.Light).Background
            && applied[1].ThreadId == uiThreadId;

        var sameBindingTask = RunCaptured(
            () => fake.Set(OverlayThemeKind.Light, forceEvent: true),
            backgroundErrors);
        var sameBindingTaskCompleted = PumpUntil(() => sameBindingTask.IsCompleted);
        Application.DoEvents();
        var bindingSameKindIgnored = sameBindingTaskCompleted && applied.Count == 2;

        var queuedTask = RunCaptured(
            () => fake.Set(OverlayThemeKind.Dark, forceEvent: true),
            backgroundErrors);
        var queuedTaskCompleted = WaitWithoutPumping(() => queuedTask.IsCompleted);
        binding.Dispose();
        binding.Dispose();
        Application.DoEvents();
        var bindingQueuedCallbackCancelledOnDispose = queuedTaskCompleted && applied.Count == 2;

        using var themedForm = new TokenStripForm();
        using var themedHighlight = new AttachmentTargetHighlightForm();
        _ = themedForm.Handle;
        _ = themedHighlight.Handle;
        var themedPresentation = OverlayPresentationBuilder.CreateWaiting(
            "theme-probe",
            DisplayField.Total,
            DisplayField.ContextPercent,
            DisplayField.Total | DisplayField.ContextPercent);
        themedForm.SetPresentation(themedPresentation);
        themedForm.BeginEditMode(ManualAttachmentRules.DefaultScalePercent);
        var themedBounds = themedForm.Bounds;
        var themedLayout = themedForm.CurrentLayout;
        var themedPresentationReference = themedForm.CurrentPresentation;
        var themedEditState = themedForm.IsEditMode;
        var themedVisibility = themedForm.Visible;
        var themedHighlightBounds = themedHighlight.Bounds;
        var themedHighlightVisibility = themedHighlight.Visible;
        var formApplyCount = 0;
        var formApplyThreads = new List<int>();
        var formSource = new ProbeOverlayThemeSource(OverlayThemeKind.Dark);
        var formBinding = new OverlayThemeBinding(
            themedHighlight,
            formSource,
            palette =>
            {
                formApplyCount++;
                formApplyThreads.Add(Environment.CurrentManagedThreadId);
                themedForm.ApplyTheme(palette);
                themedHighlight.ApplyTheme(palette);
            });
        var darkPalette = OverlayThemePalette.For(OverlayThemeKind.Dark);
        var lightPalette = OverlayThemePalette.For(OverlayThemeKind.Light);
        var formsInitialDark = formApplyCount == 1
            && themedForm.CurrentThemePalette == darkPalette
            && themedHighlight.CurrentThemePalette == darkPalette
            && themedForm.BackColor == darkPalette.Background
            && themedForm.ForeColor == darkPalette.Value
            && themedHighlight.BackColor == Color.Fuchsia
            && themedHighlight.TransparencyKey == Color.Fuchsia;

        var formLightTask = RunCaptured(
            () => formSource.Set(OverlayThemeKind.Light, forceEvent: true),
            backgroundErrors);
        var formLightTaskCompleted = PumpUntil(() => formLightTask.IsCompleted);
        var formLightApplied = PumpUntil(() => formApplyCount == 2);
        var formsBackgroundLightOnUiThread = formLightTaskCompleted
            && formLightApplied
            && themedForm.CurrentThemePalette == lightPalette
            && themedHighlight.CurrentThemePalette == lightPalette
            && themedForm.BackColor == lightPalette.Background
            && themedForm.ForeColor == lightPalette.Value
            && formApplyThreads.All(threadId => threadId == uiThreadId);
        var formsStateUnchanged = themedForm.Bounds == themedBounds
            && ReferenceEquals(themedForm.CurrentLayout, themedLayout)
            && ReferenceEquals(themedForm.CurrentPresentation, themedPresentationReference)
            && themedForm.IsEditMode == themedEditState
            && themedForm.Visible == themedVisibility
            && themedHighlight.Bounds == themedHighlightBounds
            && themedHighlight.Visible == themedHighlightVisibility;

        var formSameTask = RunCaptured(
            () => formSource.Set(OverlayThemeKind.Light, forceEvent: true),
            backgroundErrors);
        var formSameTaskCompleted = PumpUntil(() => formSameTask.IsCompleted);
        Application.DoEvents();
        var formsSameKindIgnored = formSameTaskCompleted && formApplyCount == 2;

        themedForm.EndEditMode();
        var formDarkTask = RunCaptured(
            () => formSource.Set(OverlayThemeKind.Dark, forceEvent: true),
            backgroundErrors);
        var formDarkTaskCompleted = PumpUntil(() => formDarkTask.IsCompleted);
        var formDarkApplied = PumpUntil(() => formApplyCount == 3);
        var formsSurviveTokenHandleRecreation = formDarkTaskCompleted
            && formDarkApplied
            && themedForm.CurrentThemePalette == darkPalette
            && themedHighlight.CurrentThemePalette == darkPalette
            && formApplyThreads.All(threadId => threadId == uiThreadId);

        formBinding.Dispose();
        formBinding.Dispose();
        var postDisposeFormTask = RunCaptured(
            formSource.RaiseCapturedEvent,
            backgroundErrors);
        var postDisposeFormCompleted = PumpUntil(() => postDisposeFormTask.IsCompleted);
        Application.DoEvents();
        var formsPostDisposeIgnored = postDisposeFormCompleted
            && formApplyCount == 3
            && themedForm.CurrentThemePalette == darkPalette
            && themedHighlight.CurrentThemePalette == darkPalette;

        return new
        {
            Cases = new[]
            {
                new
                {
                    Name = "theme-lifecycle",
                    Supported = true,
                    SourceInitialDark = sourceInitialDark,
                    SourceChangedOnce = sourceChangedOnce,
                    SourceSameKindIgnored = sourceSameKindIgnored,
                    SourceUnsubscribedOnce = unsubscribeCount == 1 && registeredHandler is null,
                    SourceDisposeIdempotent = unsubscribeCount == 1,
                    SourcePostDisposeIgnored = sourcePostDisposeIgnored,
                    BindingInitialDark = bindingInitialDark,
                    BindingBackgroundLightOnUiThread = bindingBackgroundLightOnUiThread,
                    BindingSameKindIgnored = bindingSameKindIgnored,
                    BindingQueuedCallbackCancelledOnDispose = bindingQueuedCallbackCancelledOnDispose,
                    BindingUnsubscribedBeforeSourceDispose = fake.UnsubscribedBeforeDispose,
                    FormsSupported = true,
                    FormsInitialDark = formsInitialDark,
                    FormsBackgroundLightOnUiThread = formsBackgroundLightOnUiThread,
                    FormsStateUnchanged = formsStateUnchanged,
                    FormsSameKindIgnored = formsSameKindIgnored,
                    FormsSurviveTokenHandleRecreation = formsSurviveTokenHandleRecreation,
                    FormsPostDisposeIgnored = formsPostDisposeIgnored,
                    NoBackgroundException = backgroundErrors.Count == 0
                }
            }
        };
    }
}

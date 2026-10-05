using System.Runtime.InteropServices;

namespace CodexTokenOverlay;

internal sealed class OverlayContext : ApplicationContext
{
    private readonly OverlaySettings _settings;
    private readonly CodexVisibleThreadMonitor _routeMonitor;
    private readonly TokenLogMonitor _monitor;
    private readonly TokenStripForm _form = new();
    private readonly AttachmentTargetHighlightForm _targetHighlight = new();
    private readonly OverlayThemeBinding _themeBinding;
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _outsideClickTimer;
    private readonly System.Windows.Forms.Timer _hostMoveTimer;
    private readonly NativeWindowEvents _windowEvents;
    private readonly ToolStripMenuItem _sessionMenuItem;
    private readonly ToolStripMenuItem _visibilityMenuItem;
    private readonly ToolStripMenuItem _pinSessionMenuItem;
    private readonly ToolStripMenuItem _adjustManualMenuItem;
    private readonly ToolStripMenuItem _saveManualMenuItem;
    private readonly ToolStripMenuItem _cancelManualMenuItem;
    private readonly ToolStripMenuItem _resetManualMenuItem;
    private readonly ToolStripMenuItem _traditionalMenuItem;
    private readonly Dictionary<AnchorMode, ToolStripMenuItem> _anchorItems = new();
    private readonly Dictionary<DisplayField, ToolStripMenuItem> _fieldItems = new();
    private readonly Dictionary<(CollapsedSlot Slot, DisplayField Field), ToolStripMenuItem>
        _collapsedFieldItems = new();
    private readonly OverlayInteractionState _interaction = new();
    private readonly OverlayAnchorTargetState _anchorTargetState = new();
    private readonly ManualAttachmentCoordinator _manualAttachment = new();
    private readonly string? _settingsPath;
    private readonly string? _pricingPath;
    private PricingRevision _prices;
    private readonly SessionCostCalculator _costCalculator = new();
    private SessionCostResult? _cost;
    private CostDetailsForm? _costDetails;
    private OverlayPresentation _presentation;
    private CodexWindowTarget? _currentTarget;
    private ManualPlacementSnapshot? _settingsSnapshotBeforeEdit;
    private bool _saveFailureNotified;
    private readonly SessionSelection _selection = new();
    private CancellationTokenSource? _readCancellation;
    private TokenSnapshot? _lastSnapshot => _selection.Snapshot;
    private bool _manuallyHidden;
    private int _pollInFlight;
    private int _disposed;
    private int _logRefreshQueued;
    private int _hostDiscoveryQueued;
    private long _logChangeVersion;
    private long _nextHostValidation;
    private string? _pendingThreadId => _selection.ThreadId;
    private ActiveThreadRouteStatus _pendingRouteStatus = new(null, 0, false, 0, null);
    private string? _manualThreadId;
    private string? _queuedRoot;
    private long _selectionRevision;
    private string? _pendingError => _selection.Error;
    private OverlayThemePalette _systemPalette = OverlayThemePalette.For(OverlayThemeKind.Dark);

    public OverlayContext(string sessionRoot, string? settingsPath = null, bool useSavedRoot = true)
    {
        _settingsPath = settingsPath;
        _settings = OverlaySettings.Load(_settingsPath);
        _pricingPath = settingsPath is null ? null : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!, "prices.json");
        var pricingLoad = PricingStore.Load(_pricingPath);
        _prices = new(1, pricingLoad.Settings);
        try { _monitor = new TokenLogMonitor(useSavedRoot ? _settings.SessionRoot ?? sessionRoot : sessionRoot); }
        catch (ArgumentException) { _monitor = new TokenLogMonitor(sessionRoot); }
        _routeMonitor = new CodexVisibleThreadMonitor(_monitor.SessionRoot);
        _manualThreadId = _settings.PinnedThreadId;
        _presentation = OverlayPresentationBuilder.CreateWaiting(
            "正在寻找当前 Codex 会话…",
            _settings.CollapsedPrimaryField,
            _settings.CollapsedSecondaryField,
            _settings.VisibleFields);
        _ = _targetHighlight.Handle;
        _themeBinding = new OverlayThemeBinding(
            _targetHighlight,
            new WindowsOverlayThemeSource(),
            ApplyTheme);

        var menu = new ContextMenuStrip();
        _sessionMenuItem = new ToolStripMenuItem("会话：等待数据") { Enabled = false };
        menu.Items.Add(_sessionMenuItem);
        _pinSessionMenuItem = new ToolStripMenuItem("锁定当前会话") { CheckOnClick = true, Checked = _manualThreadId is not null };
        _pinSessionMenuItem.CheckedChanged += (_, _) =>
        {
            _manualThreadId = _pinSessionMenuItem.Checked ? _manualThreadId ?? _pendingThreadId : null;
            _settings.PinnedThreadId = _manualThreadId;
            _settings.Save(_settingsPath);
            _selectionRevision++;
            _pinSessionMenuItem.Text = _manualThreadId is not null ? "已锁定当前会话" : "锁定当前会话";
            Tick();
        };
        menu.Items.Add(_pinSessionMenuItem);
        var selectItem = new ToolStripMenuItem("手动选择并锁定会话…");
        selectItem.Click += async (_, _) => await SelectSessionAsync();
        menu.Items.Add(selectItem);
        var directoryItem = new ToolStripMenuItem("选择 Codex 日志目录…");
        directoryItem.Click += (_, _) => SelectLogDirectory();
        menu.Items.Add(directoryItem);
        menu.Items.Add(new ToolStripSeparator());
        var pricesItem = new ToolStripMenuItem("模型价格…");
        pricesItem.Click += (_, _) => EditPrices();
        menu.Items.Add(pricesItem);
        var costsItem = new ToolStripMenuItem("费用明细…");
        costsItem.Click += (_, _) => ShowCostDetails();
        menu.Items.Add(costsItem);
        menu.Items.Add(new ToolStripSeparator());

        _adjustManualMenuItem = new ToolStripMenuItem("调整位置和大小…");
        _adjustManualMenuItem.Click += (_, _) => BeginManualEditing();
        menu.Items.Add(_adjustManualMenuItem);
        _saveManualMenuItem = new ToolStripMenuItem("完成调整") { Visible = false };
        _saveManualMenuItem.Click += (_, _) => SaveManualEditing();
        menu.Items.Add(_saveManualMenuItem);
        _cancelManualMenuItem = new ToolStripMenuItem("取消调整") { Visible = false };
        _cancelManualMenuItem.Click += (_, _) => CancelManualEditing();
        menu.Items.Add(_cancelManualMenuItem);
        _resetManualMenuItem = new ToolStripMenuItem("重置到顶部菜单空白区");
        _resetManualMenuItem.Click += (_, _) => ResetManualPlacement();
        menu.Items.Add(_resetManualMenuItem);

        _traditionalMenuItem = new ToolStripMenuItem("传统定位");
        AddAnchorMenu(_traditionalMenuItem, "顶部菜单空白区（默认）", AnchorMode.TitleBarTopRight);
        AddAnchorMenu(_traditionalMenuItem, "自动吸附", AnchorMode.Auto);
        AddAnchorMenu(_traditionalMenuItem, "窗口内右上", AnchorMode.InsideTopRight);
        AddAnchorMenu(_traditionalMenuItem, "窗口内右下", AnchorMode.InsideBottomRight);
        menu.Items.Add(_traditionalMenuItem);
        menu.Items.Add(new ToolStripSeparator());

        var collapsedFieldsMenu = new ToolStripMenuItem("收起时显示");
        var primaryMenu = new ToolStripMenuItem("左侧指标");
        var secondaryMenu = new ToolStripMenuItem("右侧指标");
        foreach (var field in DisplayFieldRules.Ordered)
        {
            var text = OverlayPresentationBuilder.GetFieldMenuText(field);
            AddCollapsedFieldMenu(primaryMenu, text, CollapsedSlot.Primary, field);
            AddCollapsedFieldMenu(secondaryMenu, text, CollapsedSlot.Secondary, field);
        }
        collapsedFieldsMenu.DropDownItems.Add(primaryMenu);
        collapsedFieldsMenu.DropDownItems.Add(secondaryMenu);
        menu.Items.Add(collapsedFieldsMenu);

        var fieldsMenu = new ToolStripMenuItem("显示字段");
        foreach (var field in DisplayFieldRules.Ordered)
        {
            AddVisibleFieldMenu(fieldsMenu, OverlayPresentationBuilder.GetFieldMenuText(field), field);
        }
        menu.Items.Add(fieldsMenu);
        var themeMenu = new ToolStripMenuItem("主题");
        foreach (var option in new[] { (Mode: "system", Text: "跟随 Windows"), (Mode: "light", Text: "浅色"), (Mode: "dark", Text: "深色") })
        {
            var item = new ToolStripMenuItem(option.Text) { Checked = _settings.ThemeMode == option.Mode, Tag = option.Mode };
            item.Click += (_, _) =>
            {
                _settings.ThemeMode = option.Mode; _settings.Save(_settingsPath);
                foreach (ToolStripMenuItem other in themeMenu.DropDownItems) other.Checked = (string?)other.Tag == option.Mode;
                ApplyTheme(_systemPalette);
            };
            themeMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(themeMenu);
        menu.Items.Add(new ToolStripSeparator());

        _visibilityMenuItem = new ToolStripMenuItem("暂时隐藏");
        _visibilityMenuItem.Click += (_, _) =>
        {
            if (_manualAttachment.IsEditing)
            {
                CancelManualEditing();
            }
            _manuallyHidden = !_manuallyHidden;
            _visibilityMenuItem.Text = _manuallyHidden ? "恢复显示" : "暂时隐藏";
            if (_manuallyHidden)
            {
                CollapseAndHide();
            }
            else
            {
                Tick();
            }
        };
        menu.Items.Add(_visibilityMenuItem);

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitOverlay();
        menu.Items.Add(exitItem);

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Information,
            Text = "Codex 会话用量",
            Visible = true,
            ContextMenuStrip = menu
        };
        if (pricingLoad.Error is not null)
            _trayIcon.ShowBalloonTip(6000, "价格配置读取失败", pricingLoad.Error, ToolTipIcon.Warning);

        _form.SetPresentation(_presentation);
        _form.CapsuleClicked += HandleCapsuleClicked;
        _form.EditPreviewChanged += HandleEditPreviewChanged;
        _form.EditGestureCompleted += HandleEditGestureCompleted;
        _form.EditSaveRequested += (_, _) => SaveManualEditing();
        _form.EditCancelRequested += (_, _) => CancelManualEditing();
        UpdateAnchorChecks();
        UpdateManualMenuState();
        UpdateFieldChecks();
        UpdateCollapsedFieldChecks();

        _timer = new System.Windows.Forms.Timer { Interval = 150 };
        _timer.Tick += (_, _) => Tick();
        _outsideClickTimer = new System.Windows.Forms.Timer { Interval = 40 };
        _outsideClickTimer.Tick += (_, _) => PollOutsidePointer();
        _hostMoveTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _hostMoveTimer.Tick += (_, _) => RefreshHostGeometry();
        _ = _form.Handle;
        _windowEvents = new NativeWindowEvents(HandleHostWindowEvent);
        _monitor.DataChanged += HandleLogChanged;
        _timer.Start();
    }

    private void HandleLogChanged()
    {
        Interlocked.Increment(ref _logChangeVersion);
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _logRefreshQueued, 1) != 0) return;
        try
        {
            _form.BeginInvoke((Action)(() =>
            {
                Interlocked.Exchange(ref _logRefreshQueued, 0);
                if (Volatile.Read(ref _disposed) != 0) return;
                if (ObserveSelection()) { RefreshPresentation(); UpdateSessionMenuText(); RefreshTrayText(); }
                RequestBackgroundPoll();
            }));
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref _logRefreshQueued, 0); }
    }

    private void SetCurrentTarget(CodexWindowTarget target)
    {
        _currentTarget = target;
        _windowEvents.Watch(target.HostWindow.Handle, CodexWindowLocator.ConfirmedProcessId(target));
        if (!_windowEvents.HasLocationHook) _hostMoveTimer.Start();
    }

    private void HandleHostWindowEvent(HostWindowEvent change)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (change == HostWindowEvent.Destroyed)
        {
            _hostMoveTimer.Stop();
            if (_manualAttachment.IsEditing) CancelManualEditing(restoreFocus: false, relayout: false);
            _currentTarget = null; _windowEvents.Watch(IntPtr.Zero, 0);
            _nextHostValidation = 0; CollapseAndHide(); return;
        }
        if (change == HostWindowEvent.MoveStarted) _hostMoveTimer.Start();
        if (change == HostWindowEvent.MoveEnded) _hostMoveTimer.Stop();
        RefreshHostGeometry();
        if (change == HostWindowEvent.Foreground && !_manualAttachment.IsEditing
            && Interlocked.Exchange(ref _hostDiscoveryQueued, 1) == 0)
        {
            _form.BeginInvoke((Action)(() =>
            {
                Interlocked.Exchange(ref _hostDiscoveryQueued, 0); Tick();
            }));
        }
    }

    private void RefreshHostGeometry()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (_manuallyHidden || _currentTarget is null
            || !CodexWindowLocator.TryRefreshKnownGeometry(_currentTarget, !_manualAttachment.IsEditing, out var target))
        {
            _hostMoveTimer.Stop(); CollapseAndHide(); return;
        }
        SetCurrentTarget(target);
        if (_manualAttachment.IsEditing)
        {
            if (_manualAttachment.ShouldApplyStaticDraft) ApplyEditDraftLayout(target);
        }
        else ApplyLayout(target);
    }

    private void AddAnchorMenu(ToolStripMenuItem menu, string text, AnchorMode mode)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) =>
        {
            if (_manualAttachment.IsEditing)
            {
                CancelManualEditing();
            }
            _settings.ManualPlacementEnabled = false;
            _settings.AnchorMode = mode;
            _settings.Save(_settingsPath);
            UpdateAnchorChecks();
            UpdateManualMenuState();
            if (_currentTarget is not null && !_manuallyHidden)
            {
                ApplyLayout(_currentTarget);
            }
        };
        _anchorItems[mode] = item;
        menu.DropDownItems.Add(item);
    }

    private void AddVisibleFieldMenu(ToolStripMenuItem parent, string text, DisplayField field)
    {
        var item = new ToolStripMenuItem(text) { CheckOnClick = false };
        item.Click += (_, _) =>
        {
            var updated = _settings.VisibleFields.HasFlag(field)
                ? _settings.VisibleFields & ~field
                : _settings.VisibleFields | field;
            if (updated == DisplayField.None)
            {
                return;
            }

            _settings.VisibleFields = updated;
            _settings.Save(_settingsPath);
            UpdateFieldChecks();
            RefreshPresentation();
            if (_currentTarget is not null && !_manuallyHidden)
            {
                ApplyLayout(_currentTarget);
            }
        };
        _fieldItems[field] = item;
        parent.DropDownItems.Add(item);
    }

    private void AddCollapsedFieldMenu(
        ToolStripMenuItem parent,
        string text,
        CollapsedSlot slot,
        DisplayField field)
    {
        var item = new ToolStripMenuItem(text) { CheckOnClick = false };
        item.Click += (_, _) =>
        {
            if (!_settings.SelectCollapsedField(slot, field))
            {
                return;
            }

            _settings.Save(_settingsPath);
            UpdateCollapsedFieldChecks();
            RefreshPresentation();
            if (_currentTarget is not null && !_manuallyHidden)
            {
                ApplyLayout(_currentTarget);
            }
        };
        _collapsedFieldItems[(slot, field)] = item;
        parent.DropDownItems.Add(item);
    }

    private void UpdateAnchorChecks()
    {
        foreach (var pair in _anchorItems)
        {
            pair.Value.Checked = pair.Key == _settings.AnchorMode;
        }
    }

    private void UpdateManualMenuState()
    {
        var editing = _manualAttachment.IsEditing;
        _adjustManualMenuItem.Visible = !editing;
        _adjustManualMenuItem.Enabled = !editing && _currentTarget is not null;
        _saveManualMenuItem.Visible = editing;
        _saveManualMenuItem.Enabled = editing && _manualAttachment.CanSave;
        _cancelManualMenuItem.Visible = editing;
        _cancelManualMenuItem.Enabled = editing;
        _resetManualMenuItem.Enabled = !editing;
        _traditionalMenuItem.Enabled = !editing;
        foreach (var pair in _anchorItems)
        {
            pair.Value.Checked = !_settings.ManualPlacementEnabled
                && pair.Key == _settings.AnchorMode;
        }
    }

    private void UpdateFieldChecks()
    {
        foreach (var pair in _fieldItems)
        {
            pair.Value.Checked = _settings.VisibleFields.HasFlag(pair.Key);
        }
    }

    private void UpdateCollapsedFieldChecks()
    {
        foreach (var pair in _collapsedFieldItems)
        {
            pair.Value.Checked = pair.Key.Slot switch
            {
                CollapsedSlot.Primary => pair.Key.Field == _settings.CollapsedPrimaryField,
                CollapsedSlot.Secondary => pair.Key.Field == _settings.CollapsedSecondaryField,
                _ => false
            };
        }
    }

    private void Tick()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var activeThreadChanged = ObserveSelection();
        _pinSessionMenuItem.Enabled = _pendingThreadId is not null;
        RefreshPresentation();
        UpdateSessionMenuText();
        RefreshTrayText();
        RequestBackgroundPoll();

        if (_manualAttachment.IsEditing)
        {
            if (_currentTarget is null
                || !CodexWindowLocator.TryRefreshKnownGeometry(
                    _currentTarget,
                    requireForeground: false,
                    out var refreshedTarget))
            {
                CancelManualEditing(restoreFocus: false, relayout: false);
                CollapseAndHide();
                return;
            }

            SetCurrentTarget(refreshedTarget);
            if (_manualAttachment.ShouldApplyStaticDraft)
            {
                ApplyEditDraftLayout(refreshedTarget);
            }
            UpdateManualMenuState();
            return;
        }

        CodexWindowTarget target = null!;
        var fast = _currentTarget is not null && Environment.TickCount64 < _nextHostValidation
            && CodexWindowLocator.TryRefreshKnownGeometry(_currentTarget, requireForeground: true, out target!);
        if (_manuallyHidden || (!fast && !CodexWindowLocator.TryGetForegroundCodexTarget(out target!)))
        {
            CollapseAndHide();
            UpdateManualMenuState();
            return;
        }

        if (activeThreadChanged)
        {
            _interaction.CollapseForHostChange();
            StopOutsideClickPolling();
        }

        if (!fast) _nextHostValidation = Environment.TickCount64 + 1_000;
        SetCurrentTarget(target!);
        ApplyLayout(target);
        UpdateManualMenuState();
    }

    private void RequestBackgroundPoll()
    {
        if (Volatile.Read(ref _disposed) != 0
            || Interlocked.CompareExchange(ref _pollInFlight, 1, 0) != 0)
        {
            return;
        }

        var uiScheduler = TaskScheduler.FromCurrentSynchronizationContext();
        var request = _selection.Request();
        var prices = _prices;
        var manualId = _manualThreadId;
        var root = _queuedRoot;
        var logVersion = Interlocked.Read(ref _logChangeVersion);
        var cancellation = _readCancellation = new CancellationTokenSource();
        _ = Task.Run(() =>
            {
                if (root is not null) _monitor.SetRoot(root);
                _monitor.PinActiveSession = false;
                cancellation.Token.ThrowIfCancellationRequested();
                _monitor.PreferredThreadId = request.ThreadId;
                if (manualId is not null) _monitor.SelectManual(manualId);
                var snapshot = _monitor.Poll(cancellationToken: cancellation.Token);
                var cost = snapshot?.Ledger is { } ledger ? _costCalculator.Calculate(ledger, prices, cancellation.Token) : null;
                return (Snapshot: snapshot, Error: _monitor.LastError, Cost: cost);
            })
            .ContinueWith(task =>
            {
                try
                {
                    if (Volatile.Read(ref _disposed) == 0)
                    {
                        ObserveSelection();
                        if (task.Status == TaskStatus.RanToCompletion)
                        {
                            if (_selection.TryPublish(request, task.Result.Snapshot, task.Result.Error))
                                _cost = CostPublication.Matches(request, _selection.Revision, _lastSnapshot, _prices.Version, task.Result.Cost)
                                    ? task.Result.Cost : null;
                            if (_queuedRoot == root) _queuedRoot = null;
                        }
                        else if (task.IsFaulted && task.Exception!.GetBaseException() is not OperationCanceledException)
                        {
                            if (_selection.TryPublish(request, null, "读取暂时失败，正在重试")) _cost = null;
                        }
                        RefreshPresentation(); UpdateSessionMenuText(); RefreshTrayText();
                    }
                }
                finally
                {
                    if (ReferenceEquals(_readCancellation, cancellation)) _readCancellation = null;
                    cancellation.Dispose();
                    Interlocked.Exchange(ref _pollInFlight, 0);
                }
                // Coalesce rapid switches to the latest selection as soon as the cancelled read exits.
                if ((request.Revision != _selection.Revision || prices.Version != _prices.Version
                    || logVersion != Interlocked.Read(ref _logChangeVersion)) && Volatile.Read(ref _disposed) == 0) RequestBackgroundPoll();
            }, CancellationToken.None, TaskContinuationOptions.None, uiScheduler);
    }

    private bool ObserveSelection()
    {
        _pendingRouteStatus = _routeMonitor.GetStatus();
        if (!_selection.Observe(_pendingRouteStatus, _manualThreadId, _selectionRevision)) return false;
        _cost = null;
        _costDetails?.SetResult(_pendingThreadId, null);
        _readCancellation?.Cancel();
        _interaction.CollapseForHostChange(); StopOutsideClickPolling();
        return true;
    }

    private void RefreshTrayText() => _trayIcon.Text = _lastSnapshot is null ? "Codex 会话用量 · 等待当前会话" :
        TrimTrayText($"Codex {OverlayPresentationBuilder.ShortThreadId(_lastSnapshot.ThreadId)} · {OverlayPresentationBuilder.FormatTokenCount(_lastSnapshot.EffectiveTotalTokens)} tok");

    internal ConversationProbeSample ReadConversationProbeSample() => new(DateTime.UtcNow,
        _pendingRouteStatus, _selection.Revision, _selection.ThreadId, _lastSnapshot?.ThreadId,
        _lastSnapshot?.EffectiveTotalTokens, _lastSnapshot?.TurnCount, _selection.Error);

    // Uses the production read/publication path with synthetic logs and no visible overlay.
    // Disabling the ordinary timer proves that notifications alone publish new usage.
    internal void StartNotificationsOnlyProbe()
    {
        _manuallyHidden = true; Tick(); _timer.Stop();
    }
    internal TokenSnapshot? ReadLiveProbeSnapshot() => _lastSnapshot;

    private void RefreshPresentation()
    {
        var following = FollowSelection.Status(_pendingRouteStatus, _manualThreadId);
        var waiting = _pendingThreadId is null ? following : $"等待会话 {OverlayPresentationBuilder.ShortThreadId(_pendingThreadId)} 的用量";
        _presentation = _lastSnapshot is null
            ? OverlayPresentationBuilder.CreateWaiting(waiting, _settings.CollapsedPrimaryField, _settings.CollapsedSecondaryField, _settings.VisibleFields)
            : OverlayPresentationBuilder.Create(_lastSnapshot, _settings.CollapsedPrimaryField, _settings.CollapsedSecondaryField, _settings.VisibleFields, _cost);
        _presentation = _presentation with { FollowText = following + (_pendingError is not null && _pendingThreadId is not null ? " · " + _pendingError : "") };
        _form.SetPresentation(_presentation);
        _costDetails?.SetResult(_pendingThreadId, _cost);
    }

    private void EditPrices()
    {
        CollapseAndHide();
        var models = _lastSnapshot?.Ledger?.Calls.Select(c => c.Model).OfType<string>().ToArray() ?? Array.Empty<string>();
        if (_lastSnapshot?.Model is { } model) models = models.Append(model).ToArray();
        using var editor = new ModelPriceForm(_prices.Settings, models, _pricingPath);
        if (editor.ShowDialog() == DialogResult.OK && editor.SavedSettings is { } saved)
        {
            var firstSetup = _prices.Settings.Profiles.Count == 0 && saved.Profiles.Count > 0;
            _prices = new(_prices.Version + 1, saved);
            _cost = null;
            _readCancellation?.Cancel();
            if (firstSetup)
            {
                _settings.VisibleFields |= DisplayField.Cost;
                _settings.Save(_settingsPath);
                UpdateFieldChecks();
            }
            RefreshPresentation(); RequestBackgroundPoll();
        }
        if (_currentTarget is not null) SetForegroundWindow(_currentTarget.HostWindow.Handle);
        Tick();
    }

    private void ShowCostDetails()
    {
        CollapseAndHide();
        if (_costDetails is null || _costDetails.IsDisposed)
        {
            _costDetails = new CostDetailsForm();
            _costDetails.FormClosed += (_, _) => _costDetails = null;
        }
        _costDetails.SetResult(_pendingThreadId, _cost);
        _costDetails.Show();
        if (_costDetails.WindowState == FormWindowState.Minimized) _costDetails.WindowState = FormWindowState.Normal;
        _costDetails.Activate();
    }

    private async Task SelectSessionAsync()
    {
        CollapseAndHide();
        var entries = await Task.Run(() => _monitor.ListSessions());
        if (Volatile.Read(ref _disposed) != 0) return;
        using var picker = new SessionPickerForm(entries);
        if (picker.ShowDialog() == DialogResult.OK && picker.SelectedThreadId is { } id)
        {
            _manualThreadId = id;
            _settings.PinnedThreadId = id;
            _settings.Save(_settingsPath);
            _pinSessionMenuItem.Checked = true;
            _selectionRevision++;
        }
        if (_currentTarget is not null) SetForegroundWindow(_currentTarget.HostWindow.Handle);
        Tick();
    }

    private void SelectLogDirectory()
    {
        using var picker = new FolderBrowserDialog { Description = "选择 .codex 目录或其中的 sessions 目录", UseDescriptionForTitle = true, SelectedPath = _monitor.SessionRoot };
        if (picker.ShowDialog() != DialogResult.OK) return;
        _queuedRoot = picker.SelectedPath;
        _settings.SessionRoot = picker.SelectedPath;
        _settings.Save(_settingsPath);
        _selectionRevision++;
        _routeMonitor.SetRoot(_queuedRoot);
        ObserveSelection(); RefreshPresentation(); RefreshTrayText(); RequestBackgroundPoll();
        if (_currentTarget is not null) SetForegroundWindow(_currentTarget.HostWindow.Handle);
    }

    private void ApplyLayout(CodexWindowTarget target)
    {
        Point? manualCenter = null;
        if (_settings.ManualPlacementEnabled)
        {
            var snapshot = SnapshotFromSettings();
            var targets = CreateAttachmentTargets(target);
            manualCenter = ManualAttachmentCoordinator.ResolveCenter(snapshot, targets);
            if (manualCenter is null)
            {
                return;
            }

            if (_anchorTargetState.ObserveAndCollapse(
                target.HostWindow.Handle.ToInt64(),
                snapshot.MainAttachment.ReferencePoint,
                _interaction))
            {
                StopOutsideClickPolling();
            }
        }
        else if (_anchorTargetState.ObserveAndCollapse(
            target.HostWindow.Handle.ToInt64(),
            AttachmentReferencePoint.TopLeft,
            _interaction))
        {
            StopOutsideClickPolling();
        }

        var layout = OverlayLayoutCalculator.Calculate(
            CreateLayoutRequest(target.HostWindow, manualCenter));
        if (_interaction.State == OverlayVisualState.Expanded
            && layout.State != OverlayVisualState.Expanded)
        {
            _interaction.CollapseForExpandedLayoutFailure();
            StopOutsideClickPolling();
            layout = OverlayLayoutCalculator.Calculate(
                CreateLayoutRequest(target.HostWindow, manualCenter));
        }

        if (layout.State == OverlayVisualState.HiddenForSpace)
        {
            _interaction.HideForSpace();
            StopOutsideClickPolling();
        }
        else
        {
            _interaction.RestoreAfterSpace();
        }

        _form.ApplyLayout(layout);
        if (layout.State == OverlayVisualState.HiddenForSpace)
        {
            _form.Hide();
        }
        else if (!_form.Visible)
        {
            _form.Show();
        }

        UpdateOutsideClickPolling();
    }

    private OverlayLayoutRequest CreateLayoutRequest(
        CodexWindowInfo hostWindow,
        Point? manualCenter = null) => new(
        hostWindow,
        _settings.AnchorMode,
        _interaction.State == OverlayVisualState.Expanded,
        _presentation.ExpandedRows.Count,
        _presentation.ShowContextProgress,
        manualCenter,
        _settings.OverlayScalePercent);

    private void BeginManualEditing()
    {
        if (_manualAttachment.IsEditing || _currentTarget is null)
        {
            return;
        }

        if (!CodexWindowLocator.TryRefreshKnownCodexTarget(
            _currentTarget,
            out var refreshedTarget))
        {
            _currentTarget = null;
            UpdateManualMenuState();
            return;
        }
        _currentTarget = refreshedTarget;

        _interaction.CollapseForHostChange();
        StopOutsideClickPolling();
        _settingsSnapshotBeforeEdit = SnapshotFromSettings();
        _saveFailureNotified = false;
        var transition = _manualAttachment.BeginEdit(
            _settingsSnapshotBeforeEdit,
            CreateAttachmentTargets(_currentTarget),
            _form.CurrentLayout);
        ApplyEditTransition(_currentTarget, transition, applyLayout: true);
        _form.BeginEditMode(transition.Draft.ScalePercent);
        if (!_form.Visible)
        {
            _form.Show();
        }
        UpdateManualMenuState();
    }

    private void HandleEditPreviewChanged(
        object? sender,
        OverlayEditPreviewEventArgs eventArgs)
    {
        _ = sender;
        if (!_manualAttachment.IsEditing || _currentTarget is null)
        {
            return;
        }

        var targets = CreateAttachmentTargets(_currentTarget);
        _manualAttachment.BeginGesturePreview();
        ManualAttachmentTransition transition;
        if (eventArgs.Kind == OverlayEditGestureKind.Move)
        {
            transition = OverlayEditMoveDispatcher.Dispatch(
                _manualAttachment,
                targets,
                eventArgs,
                CurrentCapsuleCenter(),
                point => IsCursorOnKnownHost(_currentTarget!, point),
                isCompletion: false);
            ApplyEditTransition(
                _currentTarget,
                transition,
                OverlayEditPreviewLayoutPolicy.ShouldApplyLayout(eventArgs.Kind, transition));
        }
        else
        {
            transition = _manualAttachment.PreviewResize(
                targets,
                eventArgs.FixedTopLeft,
                eventArgs.ScalePercent,
                CurrentCollapsedDisplay());
            ApplyEditTransition(_currentTarget, transition, applyLayout: true);
        }
        UpdateManualMenuState();
    }

    private void HandleEditGestureCompleted(
        object? sender,
        OverlayEditPreviewEventArgs eventArgs)
    {
        _ = sender;
        if (!_manualAttachment.IsEditing || _currentTarget is null)
        {
            return;
        }

        var targets = CreateAttachmentTargets(_currentTarget);
        _manualAttachment.EndGesturePreview();
        var transition = eventArgs.Kind == OverlayEditGestureKind.Move
            ? OverlayEditMoveDispatcher.Dispatch(
                _manualAttachment,
                targets,
                eventArgs,
                CurrentCapsuleCenter(),
                point => IsCursorOnKnownHost(_currentTarget!, point),
                isCompletion: true)
            : _manualAttachment.PreviewResize(
                targets,
                eventArgs.FixedTopLeft,
                eventArgs.ScalePercent,
                CurrentCollapsedDisplay());
        ApplyEditTransition(
            _currentTarget,
            transition,
            applyLayout: true);
        UpdateManualMenuState();
    }

    private void SaveManualEditing()
    {
        if (!_manualAttachment.IsEditing)
        {
            return;
        }
        if (!_manualAttachment.CanSave)
        {
            NotifySaveFailure("请先将状态条拖到 Codex 主窗口上。");
            return;
        }

        var original = _settingsSnapshotBeforeEdit ?? SnapshotFromSettings();
        var draft = _manualAttachment.Draft with { Enabled = true };
        ApplySnapshotToSettings(draft);
        if (!_settings.TrySave(_settingsPath))
        {
            ApplySnapshotToSettings(original);
            NotifySaveFailure("无法保存设置，请检查设置文件权限后重试。");
            return;
        }

        var committed = _manualAttachment.Commit();
        ApplySnapshotToSettings(committed.Draft);
        FinishManualEditing(restoreFocus: true, relayout: true);
    }

    private void CancelManualEditing(
        bool restoreFocus = true,
        bool relayout = true)
    {
        if (!_manualAttachment.IsEditing)
        {
            return;
        }

        var cancelled = _manualAttachment.Cancel();
        ApplySnapshotToSettings(cancelled.Draft);
        FinishManualEditing(restoreFocus, relayout);
    }

    private void ResetManualPlacement()
    {
        if (_manualAttachment.IsEditing)
        {
            return;
        }

        _settings.ResetToTitleBar();
        if (!_settings.TrySave(_settingsPath))
        {
            _trayIcon.ShowBalloonTip(
                3000,
                "Codex Token 状态条",
                "无法保存重置后的设置。",
                ToolTipIcon.Warning);
        }

        _interaction.CollapseForHostChange();
        StopOutsideClickPolling();
        UpdateManualMenuState();
        if (_currentTarget is not null && !_manuallyHidden)
        {
            ApplyLayout(_currentTarget);
        }
    }

    private void ApplyEditDraftLayout(CodexWindowTarget target)
    {
        if (!_manualAttachment.IsEditing)
        {
            return;
        }

        var transition = new ManualAttachmentTransition(
            _manualAttachment.Draft,
            IsEditing: true,
            _manualAttachment.CanSave,
            RequiresPersist: false,
            ShouldCollapse: true,
            HighlightBounds: _manualAttachment.ShouldShowStaticHighlight
                ? target.HostWindow.WindowBounds
                : null,
            ResolvedCenter: ManualAttachmentCoordinator.ResolveCenter(
                _manualAttachment.Draft,
                CreateAttachmentTargets(target)));
        ApplyEditTransition(target, transition, applyLayout: true);
    }

    private void ApplyEditTransition(
        CodexWindowTarget target,
        ManualAttachmentTransition transition,
        bool applyLayout)
    {
        if (transition.HighlightBounds is IntRect highlight && !highlight.IsEmpty)
        {
            _targetHighlight.ShowTarget(highlight);
        }
        else
        {
            _targetHighlight.ClearTarget();
        }

        if (!applyLayout || transition.ResolvedCenter is not Point center)
        {
            return;
        }

        _interaction.CollapseForHostChange();
        StopOutsideClickPolling();
        var layout = OverlayLayoutCalculator.Calculate(new OverlayLayoutRequest(
            target.HostWindow,
            _settings.AnchorMode,
            RequestExpanded: false,
            _presentation.ExpandedRows.Count,
            _presentation.ShowContextProgress,
            center,
            ScalePercent: transition.Draft.ScalePercent));
        _form.ApplyLayout(layout);
        if (layout.State == OverlayVisualState.HiddenForSpace)
        {
            _form.Hide();
        }
        else if (!_form.Visible)
        {
            _form.Show();
        }
    }

    private void FinishManualEditing(bool restoreFocus, bool relayout)
    {
        var focusTarget = _currentTarget;
        _targetHighlight.ClearTarget();
        _form.EndEditMode();
        _interaction.CollapseForHostChange();
        StopOutsideClickPolling();
        _settingsSnapshotBeforeEdit = null;
        _saveFailureNotified = false;
        UpdateManualMenuState();

        if (relayout && _currentTarget is not null && !_manuallyHidden)
        {
            ApplyLayout(_currentTarget);
        }

        if (restoreFocus
            && focusTarget is not null
            && CodexWindowLocator.TryRefreshKnownCodexTarget(focusTarget, out var refreshed))
        {
            _currentTarget = refreshed;
            SetForegroundWindow(refreshed.HostWindow.Handle);
        }
    }

    private void NotifySaveFailure(string message)
    {
        if (_saveFailureNotified)
        {
            return;
        }

        _saveFailureNotified = true;
        _trayIcon.ShowBalloonTip(
            3000,
            "Codex Token 状态条",
            message,
            ToolTipIcon.Warning);
    }

    private Point CurrentCapsuleCenter()
    {
        var layout = _form.CurrentLayout
            ?? throw new InvalidOperationException("编辑布局尚未建立。");
        return new Point(
            _form.Left + layout.CapsuleBounds.X + (layout.CapsuleBounds.Width / 2),
            _form.Top + layout.CapsuleBounds.Y + (layout.CapsuleBounds.Height / 2));
    }

    private CollapsedDisplayMode CurrentCollapsedDisplay() =>
        _form.CurrentLayout?.CollapsedDisplay ?? CollapsedDisplayMode.TwoFields;

    private ManualPlacementSnapshot SnapshotFromSettings() => new(
        _settings.ManualPlacementEnabled,
        ManualAttachmentRules.SanitizeMain(_settings.MainAttachment),
        ManualAttachmentRules.SanitizeScale(_settings.OverlayScalePercent));

    private void ApplySnapshotToSettings(ManualPlacementSnapshot snapshot)
    {
        _settings.ManualPlacementEnabled = snapshot.Enabled;
        _settings.MainAttachment = ManualAttachmentRules.SanitizeMain(snapshot.MainAttachment);
        _settings.OverlayScalePercent = ManualAttachmentRules.SanitizeScale(snapshot.ScalePercent);
    }

    private static AttachmentTargetBounds CreateAttachmentTargets(CodexWindowTarget target) => new(
        target.HostWindow.Handle.ToInt64(),
        target.HostWindow.WindowBounds,
        target.HostWindow.WorkingArea,
        target.HostWindow.Dpi);

    private bool IsCursorOnKnownHost(CodexWindowTarget target, Point point) =>
        CodexWindowLocator.IsPointOnKnownHost(
            target,
            point,
            new HashSet<long>
            {
                _form.Handle.ToInt64(),
                _targetHighlight.Handle.ToInt64()
            });

    private void HandleCapsuleClicked(object? sender, EventArgs eventArgs)
    {
        if (!_interaction.OnCapsuleMouseUp() || _currentTarget is null)
        {
            return;
        }

        if (!_interaction.ShouldPollOutsideClicks)
        {
            StopOutsideClickPolling();
        }
        ApplyLayout(_currentTarget);
    }

    private void PollOutsidePointer()
    {
        if (!_interaction.ShouldPollOutsideClicks)
        {
            StopOutsideClickPolling();
            return;
        }

        if (!PointerInput.TryGetCursorPosition(out var position))
        {
            return;
        }

        if (_interaction.OnPointerSample(
            PointerInput.ReadPressedButtons(),
            _form.ContainsScreenPoint(position)))
        {
            StopOutsideClickPolling();
            if (_currentTarget is not null)
            {
                ApplyLayout(_currentTarget);
            }
        }
    }

    private void UpdateOutsideClickPolling()
    {
        if (_interaction.ShouldPollOutsideClicks && !_manuallyHidden)
        {
            _outsideClickTimer.Start();
        }
        else
        {
            StopOutsideClickPolling();
        }
    }

    private void StopOutsideClickPolling() => _outsideClickTimer.Stop();

    private void CollapseAndHide()
    {
        _interaction.CollapseForHostChange();
        StopOutsideClickPolling();
        _form.Hide();
    }

    private void UpdateSessionMenuText()
    {
        var threadId = _lastSnapshot?.ThreadId ?? _pendingThreadId;
        var shortId = string.IsNullOrWhiteSpace(threadId)
            ? "等待识别"
            : OverlayPresentationBuilder.ShortThreadId(threadId);
        _sessionMenuItem.Text = $"会话：{shortId} · {FollowSelection.Status(_pendingRouteStatus, _manualThreadId)}";
    }

    private void ApplyTheme(OverlayThemePalette palette)
    {
        _systemPalette = palette;
        var selected = _settings.ThemeMode switch { "light" => OverlayThemePalette.For(OverlayThemeKind.Light), "dark" => OverlayThemePalette.For(OverlayThemeKind.Dark), _ => palette };
        _form.ApplyTheme(selected);
        _targetHighlight.ApplyTheme(selected);
    }

    private static string TrimTrayText(string value) =>
        value.Length <= 63 ? value : value[..63];

    private void ExitOverlay()
    {
        if (_manualAttachment.IsEditing)
        {
            CancelManualEditing(restoreFocus: false, relayout: false);
        }
        CollapseAndHide();
        _timer.Stop();
        _outsideClickTimer.Stop();
        _trayIcon.Visible = false;
        ExitThread();
        Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            if (_manualAttachment.IsEditing)
            {
                CancelManualEditing(restoreFocus: false, relayout: false);
            }
            _interaction.CollapseForHostChange();
            _timer.Stop();
            _outsideClickTimer.Stop();
            _timer.Dispose();
            _outsideClickTimer.Dispose();
            _hostMoveTimer.Dispose();
            _windowEvents.Dispose();
            _monitor.DataChanged -= HandleLogChanged;
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _readCancellation?.Cancel();
            _routeMonitor.Dispose();
            _monitor.Dispose();
            DisposeThemeAndForms();
        }
        base.Dispose(disposing);
    }

    private void DisposeThemeAndForms()
    {
        _costDetails?.Dispose();
        _themeBinding.Dispose();
        _targetHighlight.Dispose();
        _form.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);
}

internal static class OverlayEditMoveDispatcher
{
    public static ManualAttachmentTransition Dispatch(
        ManualAttachmentCoordinator coordinator,
        AttachmentTargetBounds targets,
        OverlayEditPreviewEventArgs eventArgs,
        Point capsuleCenter,
        Func<Point, bool> hostSurfaceResolver,
        bool isCompletion)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(eventArgs);
        ArgumentNullException.ThrowIfNull(hostSurfaceResolver);
        if (eventArgs.Kind != OverlayEditGestureKind.Move)
        {
            throw new ArgumentOutOfRangeException(nameof(eventArgs));
        }

        var hostSurfaceHit = hostSurfaceResolver(eventArgs.CursorScreen);
        return isCompletion
            ? coordinator.CompleteMove(
                targets,
                eventArgs.CursorScreen,
                capsuleCenter,
                hostSurfaceHit)
            : coordinator.PreviewMove(
                targets,
                eventArgs.CursorScreen,
                capsuleCenter,
                hostSurfaceHit);
    }
}

internal static class OverlayEditPreviewLayoutPolicy
{
    public static bool ShouldApplyLayout(
        OverlayEditGestureKind kind,
        ManualAttachmentTransition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        return kind != OverlayEditGestureKind.Move || !transition.CanSave;
    }
}

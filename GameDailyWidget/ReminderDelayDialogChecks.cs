using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace GameDailyWidget;

/// <summary>
/// Three real modal-window checks on the existing WPF dispatcher. Only synthetic
/// input is used; no OS input, personal settings, credentials or external services.
/// </summary>
public static class ReminderDelayDialogChecks
{
    public static Task<IReadOnlyList<string>> RunAsync()
    {
        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.FromThread(Thread.CurrentThread);
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            throw new InvalidOperationException("Reminder dialog checks require an active WPF dispatcher.");
        if (dispatcher.Thread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Reminder dialog checks require the WPF STA thread.");
        return dispatcher.CheckAccess()
            ? Task.FromResult(RunOnDispatcher(dispatcher))
            : dispatcher.InvokeAsync(() => RunOnDispatcher(dispatcher), DispatcherPriority.Normal).Task;
    }

    private static IReadOnlyList<string> RunOnDispatcher(Dispatcher dispatcher)
    {
        var results = new List<string>();
        var saved = RunModal(dispatcher, 45, (dialog, _) =>
        {
            var input = FindInput(dialog);
            var save = FindButton(dialog, "保存");
            input.Text = "0";
            Click(save);
            Assert(dialog.IsVisible && dialog.DialogResult != true && dialog.SelectedSeconds == 45,
                "Invalid input closed the dialog or changed its selected delay.");
            Assert(Descendants(dialog).OfType<TextBlock>().Any(text =>
                text.Text.Contains("请输入", StringComparison.Ordinal)
                && text.Text.Contains(LaunchReminderDelay.MaximumSeconds.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)),
                "Invalid input did not display an in-window validation error.");
            input.Text = "60";
            Click(save);
        });
        Assert(saved.Accepted == true && saved.SelectedSeconds == 60,
            "Valid input did not return sixty seconds from the real modal dialog.");
        results.Add("PASS 延时模态窗拒绝0且不关闭，随后保存60秒返回成功");

        var cancelled = RunModal(dispatcher, 45, (dialog, later) =>
        {
            FindInput(dialog).Text = "60";
            var cancel = FindButton(dialog, "取消");
            Assert(cancel.IsCancel && dialog.SelectedSeconds == 45,
                "The cancel button is not configured for cancellation or editing changed the selected delay.");
            var peer = UIElementAutomationPeer.CreatePeerForElement(cancel) ?? new ButtonAutomationPeer(cancel);
            var invoke = peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider
                ?? throw new InvalidOperationException("The cancel button has no UI Automation invoke provider.");
            // The provider invokes Button.OnClick on the dispatcher, including
            // WPF's IsCancel behavior. Raising Click alone would skip that behavior.
            invoke.Invoke();
            later(() => Assert(!dialog.IsVisible, "The real cancel action did not close the modal dialog."));
        });
        Assert(cancelled.Accepted == false && cancelled.SelectedSeconds == 45,
            "Cancel accepted changes or did not retain the original delay.");
        results.Add("PASS 延时模态窗编辑后取消返回false并保留原值");

        var reset = RunModal(dispatcher, 90, (dialog, _) =>
        {
            var input = FindInput(dialog);
            input.Text = "120";
            Click(FindButton(dialog, "恢复默认", prefix: true));
            Assert(dialog.IsVisible && dialog.DialogResult != true && dialog.SelectedSeconds == 90
                && input.Text == LaunchReminderDelay.DefaultSeconds.ToString(CultureInfo.InvariantCulture),
                "Restore default saved immediately, closed the dialog, or filled an incorrect value.");
            Click(FindButton(dialog, "保存"));
        });
        Assert(reset.Accepted == true && reset.SelectedSeconds == LaunchReminderDelay.DefaultSeconds,
            "Saving the restored default did not return the default delay.");
        results.Add("PASS 延时模态窗恢复默认只填30秒，点击保存后才返回成功");
        return results.AsReadOnly();
    }

    private static DialogOutcome RunModal(Dispatcher dispatcher, int initialSeconds,
        Action<ReminderDelayWindow, Action<Action>> exercise)
    {
        var dialog = new ReminderDelayWindow(initialSeconds);
        PreviewDisplay.Place(dialog);
        var pending = new List<DispatcherOperation>();
        Exception? failure = null;
        var closed = false;
        var loaded = false;
        var timeout = new DispatcherTimer(DispatcherPriority.Send, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(3)
        };

        void CloseDialog()
        {
            if (closed) return;
            try { dialog.Close(); }
            catch (Exception error) { failure ??= error; }
        }

        void Safely(Action action)
        {
            try { action(); }
            catch (Exception error)
            {
                failure ??= error;
                CloseDialog();
            }
        }

        void QueueAfterInput(Action action) => pending.Add(dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle, new Action(() => Safely(action))));

        RoutedEventHandler onLoaded = (_, _) =>
        {
            loaded = true;
            pending.Add(dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                new Action(() => Safely(() => exercise(dialog, QueueAfterInput)))));
        };
        EventHandler onClosed = (_, _) => { closed = true; timeout.Stop(); };
        EventHandler onTimeout = (_, _) =>
        {
            timeout.Stop();
            failure ??= new TimeoutException("Reminder delay modal check exceeded its three-second timeout.");
            CloseDialog();
        };
        dialog.Loaded += onLoaded;
        dialog.Closed += onClosed;
        timeout.Tick += onTimeout;
        try
        {
            timeout.Start();
            var accepted = dialog.ShowDialog();
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
            Assert(loaded && closed, "The real reminder dialog did not complete its modal lifetime.");
            return new DialogOutcome(accepted, dialog.SelectedSeconds);
        }
        finally
        {
            timeout.Stop();
            timeout.Tick -= onTimeout;
            dialog.Loaded -= onLoaded;
            foreach (var operation in pending)
                if (operation.Status == DispatcherOperationStatus.Pending) operation.Abort();
            CloseDialog();
            dialog.Closed -= onClosed;
        }
    }

    private static TextBox FindInput(ReminderDelayWindow dialog) => Descendants(dialog).OfType<TextBox>().Single();

    private static Button FindButton(ReminderDelayWindow dialog, string content, bool prefix = false) =>
        Descendants(dialog).OfType<Button>().Single(button => button.Content is string label
            && (prefix ? label.StartsWith(content, StringComparison.Ordinal) : label == content));

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var seen = new HashSet<DependencyObject>();
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!seen.Add(current)) continue;
            yield return current;
            foreach (var child in LogicalTreeHelper.GetChildren(current))
                if (child is DependencyObject dependency) pending.Push(dependency);
            if (current is not Visual) continue;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
                pending.Push(VisualTreeHelper.GetChild(current, index));
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record DialogOutcome(bool? Accepted, int SelectedSeconds);
}

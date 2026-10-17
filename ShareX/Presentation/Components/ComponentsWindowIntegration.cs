#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

#nullable enable

using Avalonia.Threading;
using ShareX.AvaloniaUI;
using ShareX.AvaloniaUI.Integration;
using ShareX.HelpersLib;
using ShareX.Localization;
using System;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>
/// The way in to <see cref="ComponentsWindow"/> from a feature that has just found out it
/// cannot run.
/// </summary>
public static class ComponentsWindowIntegration
{
    private static ComponentsWindow? window;

    /// <summary>
    /// Tell the user which component is missing, offer to install it, and wait.
    /// </summary>
    /// <param name="componentId">The component in the way.</param>
    /// <param name="feature">The feature that wanted it, for the headline.</param>
    /// <returns>
    /// True when the component is there by the time the window closes - which is what the
    /// caller is actually asking, and what lets a recording carry straight on instead of
    /// making the user press the same hotkey a second time.
    /// </returns>
    /// <remarks>
    /// Blocking, on the same pattern as <see cref="MessageBox"/>: the callers are
    /// TaskHelpers.CheckFFmpeg and friends, which are synchronous by the time a hotkey has
    /// reached them, and which used to block on a message box in exactly this spot.
    /// </remarks>
    public static bool Require(string componentId, string feature)
    {
        ModlightComponent? component = ModlightComponentCatalog.Find(componentId);

        if (component == null)
        {
            return false;
        }

        if (ModlightComponents.IsInstalled(component))
        {
            return true;
        }

        string headline = Helpers.SafeStringFormat(Strings.Components_RequiredHeadline, feature, component.Name());

        return Show(new ComponentsViewModel(componentId, headline));
    }

    /// <summary>The full list, from the settings page or the first run after installation.</summary>
    public static void ShowAll(string? headline = null) =>
        Dispatcher.UIThread.Post(() => ShowCore(new ComponentsViewModel(null, headline), null));

    private static bool Show(ComponentsViewModel model)
    {
        AvaloniaBootstrapper.EnsureInitialized();

        if (Dispatcher.UIThread.CheckAccess())
        {
            bool result = false;
            DispatcherFrame frame = new();
            ShowCore(model, value =>
            {
                result = value;
                frame.Continue = false;
            });
            Dispatcher.UIThread.PushFrame(frame);
            return result;
        }

        TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                ShowCore(model, value => completion.TrySetResult(value));
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });

        return completion.Task.GetAwaiter().GetResult();
    }

    private static void ShowCore(ComponentsViewModel model, Action<bool>? completed)
    {
        try
        {
            if (window != null)
            {
                // One window. A second request while one is up means "let me see it": two
                // would show two views of one folder and let the same component be
                // downloaded twice at once.
                if (!window.IsVisible)
                {
                    window.Show();
                }

                window.Activate();

                if (completed != null)
                {
                    ComponentsWindow existing = window;
                    existing.Closed += (_, _) => completed(existing.RequirementMet);
                }

                return;
            }

            ComponentsWindow created = new(model);
            window = created;

            created.Closed += (_, _) =>
            {
                window = null;
                completed?.Invoke(created.RequirementMet);
            };

            created.Show();
            created.Activate();
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception, "Components: could not open the window.");
            completed?.Invoke(false);
        }
    }
}

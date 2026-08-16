using System;
using System.Collections;
using System.Globalization;
using System.Resources;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace OpenBlade.Installer.UI;

internal static class InstallerStrings
{
    private static readonly ResourceManager Resources = new(
        "OpenBlade.Installer.UI.Properties.Resources",
        typeof(InstallerStrings).Assembly);

    internal static string Get(string english)
    {
        if (string.IsNullOrEmpty(english))
        {
            return english;
        }

        try
        {
            string? localized = Resources.GetString(english, CultureInfo.CurrentUICulture);
            return string.IsNullOrWhiteSpace(localized) ? english : localized;
        }
        catch (MissingManifestResourceException)
        {
            return english;
        }
        catch (MissingSatelliteAssemblyException)
        {
            return english;
        }
        catch (InvalidOperationException)
        {
            return english;
        }
    }

    internal static string Format(string englishFormat, params object[] arguments)
    {
        string localized = Get(englishFormat);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, localized, arguments);
        }
        catch (FormatException) when (!string.Equals(localized, englishFormat, StringComparison.Ordinal))
        {
            return string.Format(CultureInfo.CurrentCulture, englishFormat, arguments);
        }
    }

    internal static void Apply(DependencyObject root)
    {
        if (root == null)
        {
            throw new ArgumentNullException(nameof(root));
        }
        ApplyElement(root);
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is DependencyObject dependencyObject)
            {
                Apply(dependencyObject);
            }
        }
    }

    private static void ApplyElement(DependencyObject element)
    {
        if (element is Window window)
        {
            window.Title = Get(window.Title);
        }
        if (element is TextBlock textBlock && !string.IsNullOrWhiteSpace(textBlock.Text))
        {
            textBlock.Text = Get(textBlock.Text);
        }
        if (element is ContentControl contentControl && contentControl.Content is string content)
        {
            contentControl.Content = Get(content);
        }
        if (element is HeaderedContentControl headeredControl && headeredControl.Header is string header)
        {
            headeredControl.Header = Get(header);
        }

        string automationName = AutomationProperties.GetName(element);
        if (!string.IsNullOrWhiteSpace(automationName))
        {
            AutomationProperties.SetName(element, Get(automationName));
        }
        string automationHelpText = AutomationProperties.GetHelpText(element);
        if (!string.IsNullOrWhiteSpace(automationHelpText))
        {
            AutomationProperties.SetHelpText(element, Get(automationHelpText));
        }
    }
}

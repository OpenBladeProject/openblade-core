using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Border = Microsoft.UI.Xaml.Controls.Border;
using Button = Microsoft.UI.Xaml.Controls.Button;
using CheckBox = Microsoft.UI.Xaml.Controls.CheckBox;
using HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment;
using Orientation = Microsoft.UI.Xaml.Controls.Orientation;

namespace OpenBlade.Tray;

public sealed partial class TelemetryChart
{
    private sealed class AnalysisRow(Grid grid, TextBlock[] cells, Grid rangeGrid, TextBlock[] rangeCells)
    {
        public Grid Grid { get; } = grid;
        public TextBlock[] Cells { get; } = cells;
        public Grid RangeGrid { get; } = rangeGrid;
        public TextBlock[] RangeCells { get; } = rangeCells;
    }

    private readonly Dictionary<TelemetryMetric, AnalysisRow> analysisRows = [];
    private readonly Dictionary<TelemetryMetric, CheckBox> metricChecks = [];
    private readonly Dictionary<TelemetryMetric, Border> metricSwatches = [];
    private readonly Dictionary<TelemetryMetric, Line> metricLegendLines = [];
    private readonly StackPanel analysisPanel = new() { Spacing = 0 };
    private readonly StackPanel rangeStatisticsPanel = new() { Spacing = 0 };
    private readonly TextBlock tableMomentText = Text(AppTypography.Caption);
    private readonly TextBlock tableRangeText = Text(AppTypography.Caption);
    private readonly Expander rangeStatisticsExpander = new()
    {
        Header = LocalizedResources.Get("TelemetryUI_RangeStatistics", "Range statistics"),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Padding = new Thickness(0),
    };
    private Flyout? metricEditorFlyout;
    private readonly Dictionary<TelemetryMetric, Flyout> colorEditors = [];
    private Grid? analysisHeader;
    private bool tableIsNarrow;

    private UIElement CreateMetricEditor()
    {
        var content = new StackPanel { Spacing = 8, MinWidth = 280 };
        foreach (MetricChoice choice in choices)
        {
            var check = new CheckBox
            {
                Content = Text(AppTypography.Body, choice.Label),
                IsChecked = true,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            metricChecks.Add(choice.Metric, check);
            check.Checked += (_, _) => ChangeVisibility(choice, true);
            check.Unchecked += (_, _) => ChangeVisibility(choice, false);
            var swatch = new Border { Width = 22, Height = 14, CornerRadius = new CornerRadius(2) };
            metricSwatches.Add(choice.Metric, swatch);
            var colorButton = new Button { Content = swatch, MinHeight = 40, MinWidth = 44 };
            string colorName = LocalizedResources.Format("TelemetryUI_MetricColor", "{0} color", choice.Label);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(colorButton, colorName);
            ToolTipService.SetToolTip(colorButton, colorName);
            Flyout colorEditor = CreateColorEditor(choice);
            colorEditors.Add(choice.Metric, colorEditor);
            colorButton.Flyout = colorEditor;
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(check);
            Grid.SetColumn(colorButton, 1);
            row.Children.Add(colorButton);
            content.Children.Add(row);
        }
        Button reset = Command(LocalizedResources.Get("TelemetryUI_ResetColors", "Reset colors"));
        reset.Click += (_, _) =>
        {
            metricPreferences = metricPreferences.Select(value => value with { ColorHex = null }).ToArray();
            Draw();
            PreferencesChanged?.Invoke(this, EventArgs.Empty);
        };
        content.Children.Add(reset);
        content.Children.Add(Text(AppTypography.Caption,
            LocalizedResources.Get("TelemetryUI_ColorPreference", "Colors and visible metrics are saved for this user.")));
        var editor = Command(LocalizedResources.Get("TelemetryUI_MetricsAndColors", "Metrics and colors"));
        editor.HorizontalAlignment = HorizontalAlignment.Left;
        metricEditorFlyout = new Flyout { Content = content };
        editor.Flyout = metricEditorFlyout;

        // A compact labeled legend retains patterns when high contrast replaces series colors.
        var legend = new Grid { ColumnSpacing = 12, RowSpacing = 4 };
        legend.ColumnDefinitions.Add(new ColumnDefinition());
        legend.ColumnDefinitions.Add(new ColumnDefinition());
        legend.ColumnDefinitions.Add(new ColumnDefinition());
        for (int index = 0; index < choices.Length; index++)
        {
            MetricChoice choice = choices[index];
            var line = new Line { X1 = 0, X2 = 18, Y1 = 8, Y2 = 8, StrokeThickness = 2, Width = 18, Height = 16 };
            if (choice.Dashed)
            {
                line.StrokeDashArray = [4, 3];
            }

            metricLegendLines.Add(choice.Metric, line);
            var item = new Grid { ColumnSpacing = 6 };
            item.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            item.ColumnDefinitions.Add(new ColumnDefinition());
            item.Children.Add(line);
            TextBlock legendLabel = Text(AppTypography.Caption, choice.Label);
            Grid.SetColumn(legendLabel, 1);
            item.Children.Add(legendLabel);
            if (index % 3 == 0)
            {
                legend.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            Grid.SetColumn(item, index % 3);
            Grid.SetRow(item, index / 3);
            legend.Children.Add(item);
        }
        var controls = new StackPanel { Spacing = 10 };
        controls.Children.Add(editor);
        controls.Children.Add(legend);
        return controls;
    }

    private Flyout CreateColorEditor(MetricChoice choice)
    {
        var picker = new ColorPicker { IsAlphaEnabled = false, MinWidth = 260, MaxWidth = 300 };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(Text(AppTypography.BodyStrong, choice.Label));
        content.Children.Add(picker);
        Button apply = Command(LocalizedResources.Get("TelemetryUI_ApplyColor", "Apply color"));
        Button reset = Command(LocalizedResources.Get("TelemetryUI_DefaultColor", "Default color"));
        content.Children.Add(Columns(apply, reset));
        content.Children.Add(Text(AppTypography.Caption,
            LocalizedResources.Get("TelemetryUI_ColorPattern", "Line patterns also identify each metric.")));
        var flyout = new Flyout { Content = content };
        flyout.Opening += (_, _) => picker.Color = ParseColor(
            metricPreferences.Single(value => value.Metric == choice.Metric).ColorHex
            ?? (ActualTheme == ElementTheme.Dark ? choice.DarkColor : choice.LightColor));
        apply.Click += (_, _) =>
        {
            Windows.UI.Color color = picker.Color;
            SetMetricColor(choice, $"#{color.R:X2}{color.G:X2}{color.B:X2}");
            flyout.Hide();
        };
        reset.Click += (_, _) =>
        {
            SetMetricColor(choice, null);
            flyout.Hide();
        };
        return flyout;
    }

    private void SetMetricColor(MetricChoice choice, string? color)
    {
        metricPreferences = metricPreferences.Select(value => value.Metric == choice.Metric
            ? value with { ColorHex = color } : value).ToArray();
        Draw();
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ChangeVisibility(MetricChoice choice, bool visible)
    {
        if (updatingPreferences)
        {
            return;
        }

        metricPreferences = TelemetryChartPreferenceDefaults.Normalize(metricPreferences.Select(value =>
            value.Metric == choice.Metric ? value with { IsVisible = visible } : value).ToArray());
        Draw();
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshMetricEditor()
    {
        updatingPreferences = true;
        try
        {
            int visibleCount = metricPreferences.Count(value => value.IsVisible);
            foreach (MetricChoice choice in choices)
            {
                bool visible = IsMetricVisible(choice);
                if (metricChecks.TryGetValue(choice.Metric, out CheckBox? check))
                {
                    check.IsChecked = visible;
                    check.IsEnabled = !visible || visibleCount > 1;
                }
                if (metricSwatches.TryGetValue(choice.Metric, out Border? swatch))
                {
                    swatch.Background = SeriesBrush(choice);
                }

                if (metricLegendLines.TryGetValue(choice.Metric, out Line? line))
                {
                    line.Stroke = SeriesBrush(choice);
                    if (line.Parent is UIElement legendItem)
                    {
                        legendItem.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                    }
                }
            }
        }
        finally
        {
            updatingPreferences = false;
        }
    }

    private UIElement CreateAnalysisTable()
    {
        string metric = LocalizedResources.Get("TelemetryUI_TableMetric", "Metric");
        string moment = LocalizedResources.Get("TelemetryUI_TableMoment", "At sample");
        string minimum = LocalizedResources.Get("TelemetryUI_TableMinimum", "Min");
        string average = LocalizedResources.Get("TelemetryUI_TableAverage", "Average");
        string maximum = LocalizedResources.Get("TelemetryUI_TableMaximum", "Max");
        string readings = LocalizedResources.Get("TelemetryUI_TableReadings", "Readings");
        analysisHeader = TableRow([metric, moment, minimum, average, maximum, readings], true);
        analysisPanel.Children.Add(analysisHeader);
        rangeStatisticsPanel.Children.Add(TableRow([metric, minimum, average, maximum], true));
        foreach (MetricChoice choice in choices)
        {
            string label = $"{choice.Label} ({choice.Unit})";
            Grid row = TableRow([label, "—", "—", "—", "—", "0"], false);
            Grid rangeRow = TableRow([label, "—", "—", "—"], false);
            analysisRows.Add(choice.Metric, new AnalysisRow(row,
                row.Children.OfType<TextBlock>().ToArray(), rangeRow,
                rangeRow.Children.OfType<TextBlock>().ToArray()));
            analysisPanel.Children.Add(row);
            rangeStatisticsPanel.Children.Add(rangeRow);
        }
        analysisPanel.Children.Add(tableRangeText);
        tableRangeText.Margin = new Thickness(0, 12, 0, 0);
        rangeStatisticsExpander.Content = rangeStatisticsPanel;
        var table = new StackPanel { Spacing = 12 };
        table.Children.Add(Text(AppTypography.BodyStrong,
            LocalizedResources.Get("TelemetryUI_AnalysisTitle", "Selected sample and range statistics")));
        table.Children.Add(tableMomentText);
        table.Children.Add(analysisPanel);
        table.Children.Add(rangeStatisticsExpander);
        UpdateTableLayout();
        return table;
    }

    private static Grid TableRow(string[] contents, bool header)
    {
        var row = new Grid { ColumnSpacing = 8, MinHeight = 40, Padding = new Thickness(0, 8, 0, 8) };
        for (int index = 0; index < contents.Length; index++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(index == 0 ? 2.5 : 1, GridUnitType.Star) });
            TextBlock cell = Text(header ? AppTypography.BodyStrong : index == 0 ? AppTypography.Body : AppTypography.Data, contents[index]);
            cell.VerticalAlignment = VerticalAlignment.Center;
            if (index != 0)
            {
                cell.TextAlignment = TextAlignment.Right;
            }

            Grid.SetColumn(cell, index);
            row.Children.Add(cell);
        }
        return row;
    }

    private void UpdateTableLayout()
    {
        tableIsNarrow = ActualWidth < 650;
        if (analysisHeader is null)
        {
            return;
        }

        SetTableColumns(analysisHeader);
        foreach (AnalysisRow row in analysisRows.Values)
        {
            SetTableColumns(row.Grid);
        }

        rangeStatisticsExpander.Visibility = tableIsNarrow ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetTableColumns(Grid grid)
    {
        for (int index = 2; index <= 4; index++)
        {
            grid.ColumnDefinitions[index].Width = tableIsNarrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            grid.Children[index].Visibility = tableIsNarrow ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void UpdateAnalysisStatistics()
    {
        foreach (MetricChoice choice in choices)
        {
            AnalysisRow row = analysisRows[choice.Metric];
            row.Grid.Visibility = row.RangeGrid.Visibility = IsMetricVisible(choice) ? Visibility.Visible : Visibility.Collapsed;
            TelemetryMetricStatistics? stats = snapshot?.Series.FirstOrDefault(value => value.Metric == choice.Metric)?.Statistics;
            double?[] values = [stats?.Minimum, stats?.Average, stats?.Maximum];
            for (int index = 0; index < values.Length; index++)
            {
                row.Cells[index + 2].Text = row.RangeCells[index + 1].Text = FormatNumber(values[index], choice);
            }
            row.Cells[5].Text = (stats?.Count ?? 0).ToString("N0");
            string counts = LocalizedResources.Format("TelemetryUI_ReadingCounts", "{0} readings · {1} missing",
                stats?.Count ?? 0, stats?.MissingCount ?? 0);
            ToolTipService.SetToolTip(row.Cells[5], counts);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row.Cells[5], counts);
        }
        tableRangeText.Text = timeRange is null ? string.Empty
            : LocalizedResources.Format("TelemetryUI_RangeScope", "Range · {0} to {1}",
                timeRange.StartUtc.ToLocalTime().ToString("G"), timeRange.EndUtc.ToLocalTime().ToString("G"));
    }

    private void UpdateAnalysisValues(TelemetrySample? sample)
    {
        foreach (MetricChoice choice in choices)
        {
            if (!analysisRows.TryGetValue(choice.Metric, out AnalysisRow? row))
            {
                continue;
            }

            double? value = sample?.Value(choice.Metric);
            row.Cells[1].Text = FormatNumber(value, choice);
            string description = value is null
                ? LocalizedResources.Get("TelemetryUI_Unavailable", "Unavailable")
                : $"{FormatNumber(value, choice)} {choice.Unit}";
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row.Cells[1],
                $"{choice.Label}: {description}. {inspectionState.Text}");
        }
    }
}

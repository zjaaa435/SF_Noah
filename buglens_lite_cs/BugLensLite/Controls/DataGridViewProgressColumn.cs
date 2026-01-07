using System;
using System.Drawing;
using System.Windows.Forms;

namespace BugLensLite.Controls;

public sealed class DataGridViewProgressColumn : DataGridViewColumn
{
    public DataGridViewProgressColumn() : base(new DataGridViewProgressCell())
    {
        SortMode = DataGridViewColumnSortMode.NotSortable;
    }
}

public sealed class DataGridViewProgressCell : DataGridViewTextBoxCell
{
    protected override void Paint(
        Graphics graphics,
        Rectangle clipBounds,
        Rectangle cellBounds,
        int rowIndex,
        DataGridViewElementStates cellState,
        object? value,
        object? formattedValue,
        string? errorText,
        DataGridViewCellStyle cellStyle,
        DataGridViewAdvancedBorderStyle advancedBorderStyle,
        DataGridViewPaintParts paintParts)
    {
        base.Paint(graphics, clipBounds, cellBounds, rowIndex, cellState, value, formattedValue, errorText,
            cellStyle, advancedBorderStyle, paintParts & ~DataGridViewPaintParts.ContentForeground);

        int percent = 0;
        if (value is int i) percent = Math.Clamp(i, 0, 100);
        else if (value is double d) percent = Math.Clamp((int)Math.Round(d), 0, 100);
        else if (value is string s && int.TryParse(s, out var p)) percent = Math.Clamp(p, 0, 100);

        var isSelected = (cellState & DataGridViewElementStates.Selected) != 0;
        var bg = isSelected ? cellStyle.SelectionBackColor : cellStyle.BackColor;
        var fg = isSelected ? cellStyle.SelectionForeColor : cellStyle.ForeColor;

        using var bgBrush = new SolidBrush(bg);
        graphics.FillRectangle(bgBrush, cellBounds);

        // padding
        var pad = 6;
        var barRect = new Rectangle(cellBounds.X + pad, cellBounds.Y + pad, cellBounds.Width - pad * 2, cellBounds.Height - pad * 2);
        if (barRect.Width <= 2 || barRect.Height <= 2) return;

        // outline
        using var outlinePen = new Pen(Color.FromArgb(90, fg));
        graphics.DrawRectangle(outlinePen, barRect);

        // fill
        var fillW = (int)Math.Round((barRect.Width - 2) * (percent / 100.0));
        if (fillW > 0)
        {
            var fillRect = new Rectangle(barRect.X + 1, barRect.Y + 1, fillW, barRect.Height - 2);
            var fillColor = isSelected ? Color.FromArgb(160, 68, 147, 248) : Color.FromArgb(160, 68, 147, 248);
            using var fillBrush = new SolidBrush(fillColor);
            graphics.FillRectangle(fillBrush, fillRect);
        }

        // text
        var text = percent <= 0 ? "-" : $"{percent}%";
        TextRenderer.DrawText(graphics, text, cellStyle.Font, cellBounds, fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}







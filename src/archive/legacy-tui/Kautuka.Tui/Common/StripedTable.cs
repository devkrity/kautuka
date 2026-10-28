using Spectre.Console;
using Spectre.Console.Rendering;

namespace Kautuka.Tui.Common;

internal readonly record struct TableCellValue(string Content, bool IsMarkup = false)
{
    public static TableCellValue Plain(string? content)
        => new(content ?? "-", false);

    public static TableCellValue Markup(string content)
        => new(content, true);

    public static implicit operator TableCellValue(string content)
        => Plain(content);
}


internal static class StripedTable
{
    public static Table CreateRounded()
        => new()
        {
            Border = TableBorder.Rounded,
            ShowRowSeparators = true,
        };

    public static Table AddStripedRow(this Table table, params TableCellValue[] cells)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(cells);

        IRenderable[] renderables = new IRenderable[cells.Length];

        for (int index = 0; index < cells.Length; index++)
        {
            TableCellValue cell = cells[index];
            string content = cell.IsMarkup ? cell.Content : Markup.Escape(cell.Content);
            renderables[index] = new Markup(content);
        }

        table.AddRow(renderables);
        return table;
    }
}

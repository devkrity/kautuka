using System.Text;

using Spectre.Console;
using Spectre.Console.Rendering;

namespace Kautuka.Tui.Cli;

internal interface IShellScreen<T>
    where T : class
{
    string Title { get; }

    IRenderable Body { get; }

    IRenderable Navigation { get; }

    string Footer { get; }

    bool TryHandleKey(ConsoleKeyInfo key, out T? selection);
}


internal sealed class ShellChoiceScreen<T>(string title, IRenderable body, List<T> choices, string footer) : IShellScreen<T>
    where T : class
{
    private readonly IRenderable body = body;
    private readonly List<T> choices = choices;
    private int selectedIndex;

    public string Title { get; } = title;

    public IRenderable Body => body;

    public string Footer { get; } = footer;

    public IRenderable Navigation => BuildNavigation();

    public bool TryHandleKey(ConsoleKeyInfo key, out T? selection)
    {
        selection = null;

        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
            case ConsoleKey.K:
                selectedIndex = selectedIndex == 0 ? choices.Count - 1 : selectedIndex - 1;
                return false;
            case ConsoleKey.DownArrow:
            case ConsoleKey.J:
                selectedIndex = selectedIndex == choices.Count - 1 ? 0 : selectedIndex + 1;
                return false;
            case ConsoleKey.Home:
                selectedIndex = 0;
                return false;
            case ConsoleKey.End:
                selectedIndex = choices.Count - 1;
                return false;
            case ConsoleKey.Enter:
                selection = choices[selectedIndex];
                return true;
            default:
                if (!char.IsDigit(key.KeyChar))
                {
                    return false;
                }

                int numericIndex = key.KeyChar - '1';
                if (numericIndex < 0 || numericIndex >= choices.Count)
                {
                    return false;
                }

                selectedIndex = numericIndex;
                selection = choices[selectedIndex];
                return true;
        }
    }

    private Rows BuildNavigation()
    {
        List<IRenderable> rows = [];
        for (int index = 0; index < choices.Count; index++)
        {
            string label = Markup.Escape(choices[index]?.ToString() ?? string.Empty);
            string line = index == selectedIndex
                ? $"[black on deepskyblue1]{(index + 1).ToString().PadLeft(2)}  {label}[/]"
                : $"[grey]{(index + 1).ToString().PadLeft(2)}[/]  {label}";
            rows.Add(new Markup(line));
        }

        return new Rows([.. rows]);
    }
}

internal sealed class ShellTextInputScreen(string title, IRenderable contextBody, string prompt, bool secret, string footer, string? initialValue = null) : IShellScreen<string>
{
    private readonly IRenderable contextBody = contextBody;
    private readonly string prompt = prompt;
    private readonly bool secret = secret;
    private readonly StringBuilder buffer = new(initialValue ?? string.Empty);

    public string Title { get; } = title;

    public IRenderable Body => new Rows(contextBody, BuildInputPanel());

    public IRenderable Navigation => new Panel("[grey]Type to edit[/]\n[grey]Backspace removes[/]\n[grey]Enter submits[/]")
    {
        Header = new PanelHeader("Input", Justify.Left),
        Border = BoxBorder.Rounded,
    };

    public string Footer { get; } = footer;

    public bool TryHandleKey(ConsoleKeyInfo key, out string? selection)
    {
        selection = null;

        switch (key.Key)
        {
            case ConsoleKey.Enter:
                selection = buffer.ToString();
                return true;
            case ConsoleKey.Backspace:
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                }

                return false;
            default:
                if (!char.IsControl(key.KeyChar))
                {
                    buffer.Append(key.KeyChar);
                }

                return false;
        }
    }

    private Panel BuildInputPanel()
    {
        string value = secret ? new string('•', buffer.Length) : buffer.ToString();
        string body = string.IsNullOrEmpty(value)
            ? $"[grey]{Markup.Escape(prompt)}[/]\n[grey]_[/]"
            : $"[grey]{Markup.Escape(prompt)}[/]\n{Markup.Escape(value)}[grey]_[/]";

        return new Panel(body)
        {
            Header = new PanelHeader("Editor", Justify.Left),
            Border = BoxBorder.Rounded,
        };
    }
}

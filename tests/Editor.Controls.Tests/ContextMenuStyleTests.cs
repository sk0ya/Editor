using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Editor.Controls.Themes;
using Xunit;

namespace Editor.Controls.Tests;

/// <summary>
/// Right-click menu chrome. The menu is built fresh on every right-click and the host appends its
/// own items through <see cref="VimEditorControl.ContextMenuBuilding"/>, so the things that must
/// hold are: a menu item that has children can actually open (the item template used to have no
/// Popup at all, which silently killed every host submenu), host items — including ones nested in
/// a submenu — get the editor's own look instead of WPF's default light menu, and the colors come
/// from the active theme rather than a hardcoded dark palette.
/// </summary>
public sealed class ContextMenuStyleTests
{
    private static ContextMenu BuildMenu(VimEditorControl control)
        => (ContextMenu)typeof(VimEditorControl)
            .GetMethod("BuildContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(control, null)!;

    [Fact]
    public void Menu_item_template_can_host_a_submenu()
    {
        WpfTestHost.Run(() =>
        {
            var control = new VimEditorControl();
            var style = (Style)control.FindResource("EditorMenuItem");
            var template = (ControlTemplate)style.Setters.OfType<Setter>()
                .Single(setter => setter.Property == Control.TemplateProperty).Value;

            var content = template.LoadContent();
            var popup = Descendants(content).OfType<Popup>().SingleOrDefault();

            Assert.NotNull(popup);
            Assert.Contains(Descendants(popup!.Child), d => d is Panel { IsItemsHost: true });
        });
    }

    [Fact]
    public void Host_items_and_their_submenu_children_get_the_editor_look()
    {
        WpfTestHost.Run(() =>
        {
            var control = new VimEditorControl();
            control.ContextMenuBuilding += (_, e) =>
            {
                var parent = new MenuItem { Header = "Host submenu" };
                parent.Items.Add(new MenuItem { Header = "Nested" });
                e.Menu.Items.Add(parent);
            };

            var menu = BuildMenu(control);

            // 暗黙スタイルで載せるので、ホストが足した項目とその子孫まで同じ Style へ解決される
            // （以前はトップレベルへ一段だけ Style を代入しており、サブメニューの中は既定の白いメニューだった）。
            Assert.Same(control.FindResource("EditorMenuItem"), menu.Resources[typeof(MenuItem)]);
            Assert.Same(control.FindResource("EditorMenuSeparator"), menu.Resources[typeof(Separator)]);
            // メニュー内の Separator は暗黙スタイルではなく SeparatorStyleKey で決まる。
            // ここを上書きしないと、ホストが足した区切り線だけ既定の白い線で描かれる。
            Assert.Same(control.FindResource("EditorMenuSeparator"),
                menu.Resources[MenuItem.SeparatorStyleKey]);

            var host = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Host submenu"));
            Assert.True(host.HasItems);
        });
    }

    /// <summary>ネイティブ項目とホスト項目がそれぞれ区切り線を足すので、
    /// 末尾が区切り線で終わる／区切り線が連続する形は必ず起きる。落とすのはメニュー側の責務。</summary>
    [Fact]
    public void Leading_trailing_and_doubled_separators_are_dropped()
    {
        WpfTestHost.Run(() =>
        {
            var control = new VimEditorControl();
            control.ContextMenuBuilding += (_, e) =>
            {
                e.Menu.Items.Add(new Separator());
                e.Menu.Items.Add(new Separator());
                e.Menu.Items.Add(new MenuItem { Header = "Host" });
                e.Menu.Items.Add(new Separator());
            };

            var menu = BuildMenu(control);

            Assert.IsNotType<Separator>(menu.Items[0]);
            Assert.IsNotType<Separator>(menu.Items[^1]);
            for (int i = 1; i < menu.Items.Count; i++)
                Assert.False(menu.Items[i] is Separator && menu.Items[i - 1] is Separator,
                    "区切り線が連続している");
        });
    }

    /// <summary>配色は固定値ではなく今のテーマから引く（テーマを変えても真っ黒なメニューが出ていた）。</summary>
    [Fact]
    public void Menu_colors_come_from_the_active_theme()
    {
        WpfTestHost.Run(() =>
        {
            var control = new VimEditorControl();
            control.SetTheme(EditorTheme.Nord);
            var menu = BuildMenu(control);

            Assert.Same(EditorTheme.Nord.LineNumberBg, menu.Resources["EditorMenuBackground"]);
            Assert.Same(EditorTheme.Nord.Foreground, menu.Resources["EditorMenuForeground"]);
            Assert.Same(EditorTheme.Nord.CurrentLineBg, menu.Resources["EditorMenuHoverBackground"]);
        });
    }

    /// <summary>見出しはホストが差し替えられる（日本語アプリに埋め込んだとき英語が混ざらないように）。</summary>
    [Fact]
    public void Host_can_replace_the_native_item_labels()
    {
        WpfTestHost.Run(() =>
        {
            var control = new VimEditorControl(new VimEditorControlOptions
            {
                ContextMenuLabels = new EditorContextMenuLabels { Paste = "貼り付け" },
            });

            var menu = BuildMenu(control);

            Assert.Contains(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "貼り付け"));
            Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "Paste"));
        });
    }

    /// <summary>折り返しは「今の見え方」の設定であって、キャレットに対して何かをする操作ではない。
    /// 右クリックの行を1つ使う価値がないので Alt+Z / <c>:set wrap</c> だけに置く。</summary>
    [Fact]
    public void Word_wrap_is_not_a_context_menu_row()
    {
        WpfTestHost.Run(() =>
        {
            var menu = BuildMenu(new VimEditorControl());

            Assert.DoesNotContain(menu.Items.OfType<MenuItem>(),
                item => item.Header?.ToString()?.Contains("Word Wrap", StringComparison.Ordinal) == true);
        });
    }

    /// <summary>「どこかへ行く」操作は1つの入口にまとめ、ホストの移動系もそこへ足させる
    /// （移動の入口が2か所に割れると、探すのに両方見ることになる）。</summary>
    [Fact]
    public void Navigation_lives_in_one_submenu_the_host_can_extend()
    {
        WpfTestHost.Run(() =>
        {
            // ホスト定義プロバイダを渡すと、言語サーバーが無くても移動の段が組まれる。
            var control = new VimEditorControl(new VimEditorControlOptions
            {
                HostDefinitionProvider = (_, _, _, _, _) =>
                    Task.FromResult<(string Uri, int Line, int Column)?>(null),
            });
            MenuItem? navigate = null;
            control.ContextMenuBuilding += (_, e) =>
            {
                navigate = e.NavigateMenu;
                e.NavigateMenu?.Items.Add(new MenuItem { Header = "Peek Definition" });
            };

            var menu = BuildMenu(control);

            Assert.NotNull(navigate);
            Assert.Contains(navigate!, menu.Items.OfType<MenuItem>());
            Assert.Contains(navigate.Items.OfType<MenuItem>(),
                item => Equals(item.Header, EditorContextMenuLabels.Default.GoToDefinition));
            Assert.Contains(navigate.Items.OfType<MenuItem>(),
                item => Equals(item.Header, "Peek Definition"));
            // トップレベルには移動先が散らばらない。
            Assert.DoesNotContain(menu.Items.OfType<MenuItem>(),
                item => Equals(item.Header, EditorContextMenuLabels.Default.FindReferences));
        });
    }

    /// <summary>論理ツリーを辿る（テンプレートを読み込んだだけの段階では、
    /// ScrollViewer などの内側はまだビジュアルツリーに現れない）。</summary>
    private static IEnumerable<DependencyObject> Descendants(DependencyObject? root)
    {
        if (root is null) yield break;
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Descendants(child))
                yield return descendant;
    }
}

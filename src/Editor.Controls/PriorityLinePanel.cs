using System.Windows;
using System.Windows.Controls;

namespace Editor.Controls;

/// <summary>
/// 子を横 1 列に並べ、幅は <see cref="PriorityProperty"/> の小さい順に配る（同じ値は並び順）。
/// 幅が足りなければ優先の低い子から外れる（0 幅）。「…」で省略できる TextBlock だけは残りの幅まで縮む。
/// 並ぶ順は子の順のまま。
/// ステータスバーのファイル欄で「変更印 → ファイル名 → 改行形式 → 文字コード」の順に残すために使う
/// （StackPanel だと幅が足りないとき後ろの印が枠の外へ出て、ファイル名は省略されなかった）。
/// </summary>
internal sealed class PriorityLinePanel : Panel
{
    public static readonly DependencyProperty PriorityProperty = DependencyProperty.RegisterAttached(
        "Priority", typeof(int), typeof(PriorityLinePanel),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static int GetPriority(UIElement element) => (int)element.GetValue(PriorityProperty);
    public static void SetPriority(UIElement element, int value) => element.SetValue(PriorityProperty, value);

    private double[] _widths = [];

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren;
        _widths = new double[children.Count];
        var order = Enumerable.Range(0, children.Count)
            .OrderBy(i => children[i] is { } c ? GetPriority(c) : 0)
            .ThenBy(i => i);

        var remaining = availableSize.Width;
        double height = 0, total = 0;
        foreach (var i in order)
        {
            var child = children[i];
            if (child is null) continue;
            // 「…」で省略できる文字（TextTrimming あり）だけは残りの幅に縮める。それ以外（[dos] などの印）は
            // 途中で切れると [utf-8 のように別の文字に見えるので、収まらなければ丸ごと 0 幅にする。
            var avail = Math.Max(0, remaining);
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            if (child.DesiredSize.Width > avail)
                child.Measure(new Size(child is TextBlock { TextTrimming: not TextTrimming.None } ? avail : 0, availableSize.Height));
            var w = Math.Min(child.DesiredSize.Width, avail);
            _widths[i] = w;
            remaining -= w;
            total += w;
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(total, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = InternalChildren;
        double x = 0;
        for (var i = 0; i < children.Count; i++)
        {
            var w = i < _widths.Length ? _widths[i] : 0;
            children[i]?.Arrange(new Rect(x, 0, w, finalSize.Height));
            x += w;
        }
        return finalSize;
    }
}

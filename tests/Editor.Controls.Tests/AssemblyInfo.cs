// WPF のテストは WpfTestHost の STA ディスパッチャ 1 本を共有する。並列に走らせると、別クラスの
// テストがウィンドウを表示した拍子にこちらのウィンドウが非アクティブになり、ホバーのポップアップ
// （非アクティブで閉じるのが正しい挙動）が消える——製品側ではなくテストの直列化で解く。
[assembly: CollectionBehavior(DisableTestParallelization = true)]

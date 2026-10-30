namespace Dispatch.Core;

public static class SuiteCatalog
{
    public static Suite[] All() =>
    [
        new("mock-sw", "模拟 SolidWorks 独占测试（不连接软件）", ["sw-main", "desktop-ui"], 700, 5000, "pass"),
        new("mock-headless", "模拟后台测试", ["headless-slot"], 150, 5000, "pass"),
        new("mock-fail", "模拟断言失败", ["sw-main", "desktop-ui"], 100, 5000, "fail"),
        new("mock-crash", "模拟执行器崩溃", ["sw-main", "desktop-ui"], 100, 5000, "crash"),
        new("mock-timeout", "模拟超时", ["sw-main", "desktop-ui"], 10000, 500, "pass"),
        new("mock-cleanup-fail", "模拟清理不确定", ["sw-main", "desktop-ui"], 100, 5000, "cleanup-fail"),
        new("sw-readonly", "SolidWorks 只读连接检查", ["sw-main"], 0, 15000, "readonly", false, "solidworks-readonly")
    ];
    public static Suite Get(string id) => All().SingleOrDefault(s => s.Id == id)
        ?? throw new DispatchException("SUITE_NOT_ALLOWED", "只允许已登记的内置测试套件。");
}

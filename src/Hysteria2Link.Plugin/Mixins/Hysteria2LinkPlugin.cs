using System.Windows;
using System.Windows.Controls;
using PCL;
using PCL.Mixin;
using Hysteria2Link.Plugin.Services;
using Hysteria2Link.Plugin.UI;

namespace Hysteria2Link.Plugin.Mixins;

[Mixin("PCL.PageToolsLeft", Priority = 900)]
internal static class PageToolsLeftMixin
{
    [Inject(".ctor", At = MixinAt.Return)]
    private static void AttachHysteriaEntry([This] PageToolsLeft page)
    {
        PluginRuntime.Initialize();
        PluginNavigation.Attach(page);
    }

    [Inject("PageGet", At = MixinAt.Head, Cancellable = true)]
    private static void ResolveHysteriaPage(
        [This] PageToolsLeft page,
        [Arg(0)] FormMain.PageSubType? id,
        CallbackInfo<object> callback)
    {
        if ((id ?? page.pageID) == PluginNavigation.HysteriaLinkPage)
            callback.SetReturnValue(PluginRuntime.Page);
    }
}

internal static class PluginRuntime
{
    private static readonly object SyncRoot = new();
    private static bool _initialized;
    private static PageToolsHysteria2Link? _page;

    public static HysteriaSessionService Service { get; private set; } = null!;

    public static PageToolsHysteria2Link Page => _page ??= new PageToolsHysteria2Link();

    public static void Initialize()
    {
        lock (SyncRoot)
        {
            if (_initialized)
                return;

            var dataDirectory = Path.Combine(PCL.Core.App.Paths.Plugins, "data", "xjh2009.hysteria.link");
            Service = new HysteriaSessionService(dataDirectory);
            if (System.Windows.Application.Current is not null)
                System.Windows.Application.Current.Exit += (_, _) => Service.Dispose();
            _initialized = true;
        }
    }
}

internal static class PluginNavigation
{
    public const FormMain.PageSubType HysteriaLinkPage = (FormMain.PageSubType)0x48593201;
    private const string CategoryName = "TextHysteria2LinkCategory";
    private const string ItemName = "ItemHysteria2Link";

    public static void Attach(PageToolsLeft page)
    {
        if (page.FindName(ItemName) is MyListItem)
            return;

        var panel = page.FindName("PanItem") as StackPanel
                    ?? throw new InvalidOperationException("PageToolsLeft.PanItem 不存在。");
        var item = new MyListItem
        {
            Name = ItemName,
            IsScaleAnimationEnabled = false,
            Type = MyListItem.CheckType.RadioBox,
            Tag = ((int)HysteriaLinkPage).ToString(),
            MinPaddingRight = 35,
            Height = 36,
            VerticalAlignment = VerticalAlignment.Top,
            Title = "Hysteria P2P",
            LogoScale = 0.9,
            SvgIcon = "lucide/network"
        };
        item.Check += (_, _) => page.PageChange(HysteriaLinkPage);

        var anchor = page.FindName("ItemCloudflareLink") as UIElement
                     ?? page.FindName("ItemGameLink") as UIElement;
        if (anchor is not null)
        {
            var anchorIndex = panel.Children.IndexOf(anchor);
            panel.Children.Insert(anchorIndex + 1, item);
            page.RegisterName(ItemName, item);
            return;
        }

        if (page.FindName("TextGameLinkCategory") is UIElement existingCategory)
        {
            var categoryIndex = panel.Children.IndexOf(existingCategory);
            panel.Children.Insert(categoryIndex + 1, item);
            page.RegisterName(ItemName, item);
            return;
        }

        var category = new TextBlock
        {
            Name = CategoryName,
            Text = "联机",
            Margin = new Thickness(13, 5, 5, 3),
            Opacity = 0.6,
            FontSize = 12
        };
        panel.Children.Insert(0, category);
        panel.Children.Insert(1, item);
        page.RegisterName(CategoryName, category);
        page.RegisterName(ItemName, item);
    }
}

namespace RestaurantPOS.Web;

// Browser preference, also mutable inside the live circuit without navigation.
public sealed class UiText(IHttpContextAccessor accessor)
{
    public bool Chinese { get; private set; } = accessor.HttpContext?.Request.Cookies["335.language"] == "zh";
    public event Action? Changed;
    public void SetLanguage(bool chinese)
    {
        if (Chinese == chinese) return;
        Chinese = chinese; Changed?.Invoke();
    }
    public string this[string english] => Chinese && Translations.TryGetValue(english, out var value) ? value : english;
    public string Pick(string english, string chinese) => Chinese ? chinese : english;
    public string Payment(string method) => this[method];
    public string Destination(string value) => value.StartsWith("Table ", StringComparison.Ordinal) ? $"{this["Table"]} {value[6..]}" : this[value];
    public string Status(RestaurantPOS.Core.OrderStatus status) => status == RestaurantPOS.Core.OrderStatus.Closed ? this["Settled"] : this["Partially paid"];
    private static readonly Dictionary<string, string> Translations = new()
    {
        ["No settled bills today."] = "当天暂无已结账单。",
        ["Included"] = "已含", ["Choose an option in each group."] = "请在每组选项中选择一项。",
        ["Rice & noodles"] = "饭与面", ["Drinks"] = "饮料", ["Standard"] = "标准", ["Regular"] = "普通", ["Large"] = "大份", ["Hot"] = "热", ["Iced"] = "冰",
        ["Increase"] = "增加", ["Decrease"] = "减少", ["Loading menu and tables…"] = "正在载入菜单和桌位…",
        ["Could not load orders. Use Refresh bills to retry."] = "无法载入订单，请刷新账单重试。", ["Could not confirm payment. Retry with the same details, or refresh the bill to check its status."] = "无法确认付款，请以相同内容重试，或刷新账单查看状态。",
        ["Payment recorded. Refresh to load the current bill."] = "付款已记录，请刷新载入最新账单。", ["Payment complete. Takeaway order closed."] = "付款完成，打包订单已结单。",
        ["Open order"] = "未结订单", ["Manage order"] = "管理订单", ["Order"] = "订单", ["items"] = "项", ["remaining"] = "剩余", ["each"] = "每份",
        ["Choose a table."] = "选择桌位。", ["WAITER WORKSPACE"] = "点餐工作区", ["Every table, ready at a glance."] = "各桌状态，一目了然。",
        ["Add items to an existing order"] = "为现有订单添加餐点", ["Choose an available table to start an order, or an occupied table to add more food."] = "选择空桌开始点餐，或选择使用中的桌位加餐。",
        ["No active tables are configured. You can still create a takeaway order."] = "尚未设置桌位，您仍可创建打包订单。", ["No items match your search."] = "没有符合搜索条件的餐点。",
        ["Something delicious starts here."] = "从这里开始点餐。", ["Select food to choose a variation and add it to your order."] = "选择餐点及规格后加入订单。",
        ["Order notes"] = "订单备注", ["Refresh data"] = "刷新资料", ["Select the items to cancel. Quantities cannot exceed what remains in the order."] = "选择要取消的品项，数量不可超过剩余数量。",
        ["A payment has been recorded. Complete the remaining balance before closing this order."] = "已记录付款，请付清余款后结单。", ["Adding items to an existing order"] = "为现有订单添加餐点", ["New order"] = "新订单",
        ["Sending to kitchen…"] = "正在发送到厨房…", ["Notifying kitchen..."] = "正在通知厨房…", ["Cancel selected items & notify kitchen"] = "取消选中品项并通知厨房", ["Customize +"] = "自定义 +",
        ["Take an order, keep the kitchen moving,"] = "快速点餐，让厨房有序运作，", ["and bring every table together."] = "为每桌客人提供顺畅服务。",
        ["Tables, takeaway, and those"] = "堂食、打包以及", ["little special requests."] = "客人的特别要求。", ["See new tickets and let the"] = "查看新订单，并让", ["team know what is ready."] = "同事知道餐点已完成。",
        ["View every order clearly,"] = "清楚查看每份订单，", ["right down to the last item."] = "掌握每项账单明细。", ["01 / WAITER"] = "01 / 点餐", ["02 / KITCHEN"] = "02 / 厨房", ["03 / CASHIER"] = "03 / 收银",
        ["MADE TO SERVE"] = "用心服务", ["THE NEXT ORDER"] = "下一份订单", ["Freshly prepared."] = "新鲜现做。", ["No remaining items."] = "没有剩余品项。",
        ["Any instructions for the whole order?"] = "整份订单有任何特别要求吗？", ["For example: no chili, less sugar…"] = "例如：不要辣、少糖…", ["Search the menu…"] = "搜索菜单…", ["Add a note for the kitchen if needed"] = "如有需要，请给厨房留言",
        ["Select table"] = "选择桌位", ["Type"] = "方式", ["Food step"] = "餐点", ["Start order"] = "开始点餐", ["seats"] = "座位", ["No remaining items"] = "没有剩余品项",
        ["Reference:"] = "付款备注：", ["Cash received RM"] = "实收现金 RM", ["Change RM"] = "找零 RM", ["CLOSED · Balance settled"] = "已结单 · 余额已付清",
        ["Bill data is unavailable. Use Refresh bills to try again."] = "账单资料暂不可用，请刷新重试。", ["Loading orders…"] = "正在载入订单…", ["Order sent to the kitchen successfully."] = "订单已发送到厨房。",
        ["PAID / CHECKED OUT · Continue preparing this ticket"] = "已付款／结单 · 请继续准备餐点",
        ["Accounting"] = "账务", ["Statistics"] = "统计", ["Overview"] = "总览", ["Take orders"] = "点餐", ["Kitchen display"] = "订单", ["Cashier"] = "收银",
        ["WORKSPACE"] = "工作区", ["Restaurant workspace"] = "餐厅工作区", ["Made for the everyday rush."] = "让每天的服务更顺畅。", ["335 Restaurant POS"] = "335 餐厅收银系统",
        ["Sign in"] = "登录", ["Sign out"] = "退出", ["Back"] = "返回", ["Refresh"] = "刷新", ["Refresh bills"] = "刷新账单",
        ["Date"] = "日期", ["Purchases"] = "采购开销", ["Daily sales"] = "当日销售", ["Purchase description"] = "采购内容", ["Amount (RM)"] = "金额（RM）",
        ["For example: ice bag, vegetables"] = "例如：冰袋、蔬菜", ["Save purchase"] = "保存采购", ["Saving..."] = "保存中…", ["Purchase saved."] = "采购已保存。",
        ["No purchases recorded for this date."] = "当天尚无采购记录。", ["No payments received for this date."] = "当天尚无收款。", ["Description"] = "内容", ["Amount"] = "金额",
        ["Sales received"] = "销售收款", ["Total purchases"] = "采购合计", ["Sales less purchases"] = "销售减采购", ["This is a cash comparison, not net profit."] = "此金额是收支比较，并非净利润。",
        ["Bill"] = "账单", ["Order date"] = "下单日期", ["Last payment"] = "最后收款", ["Destination"] = "用餐方式", ["Status"] = "状态", ["Method"] = "方式",
        ["Bill total"] = "账单总额", ["Received today"] = "当日收款", ["Total paid"] = "累计已付", ["Settled"] = "已结清", ["Partially paid"] = "部分付款", ["Table"] = "桌号",
        ["Takeaway"] = "打包", ["Take away"] = "打包", ["Dine-in"] = "吃", ["Day"] = "日", ["Month"] = "月", ["Year"] = "年", ["Period"] = "周期", ["Apply"] = "查看",
        ["Payments"] = "收款笔数", ["Bills paid towards"] = "收款账单数", ["Sales trend"] = "销售趋势", ["Top selling items"] = "畅销排行", ["All"] = "全部", ["Food"] = "食品", ["Beverages"] = "饮料",
        ["Item"] = "品项", ["Category"] = "分类", ["Quantity sold"] = "售出数量", ["Revenue"] = "销售额", ["No sales in this period."] = "此期间尚无销售。",
        ["No paid items in this category."] = "此分类尚无已付款品项。", ["Ranked by revenue; quantities include paid items only."] = "按销售额排名，仅统计已付款品项数量。",
        ["Reports use Malaysia time and actual payments, including split payments. Change is excluded."] = "按马来西亚时间及实际收款统计，包含分单付款，不包含找零。",
        ["Loading..."] = "载入中…", ["Could not load data. Please try again."] = "无法载入资料，请重试。", ["Could not save. Retry with the same details."] = "无法确认保存，请以相同内容重试。",
        ["Cash"] = "现金", ["QR"] = "二维码", ["Transfer"] = "转账", ["Card"] = "银行卡", ["Other"] = "其他",
        ["Order bill"] = "账单", ["Settled bills"] = "已结账单", ["No settled bills yet."] = "暂无已结账单。", ["View receipt"] = "查看收据", ["View bill"] = "查看账单",
        ["Closed bill / receipt"] = "已结账单／收据", ["Provisional bill"] = "待付款账单", ["Print bill"] = "打印账单", ["Payment"] = "付款", ["Person"] = "付款人", ["Remaining bill"] = "剩余金额",
        ["Split bill"] = "分单付款", ["Pay full remaining bill"] = "支付全部余款", ["Select all remaining"] = "全选剩余品项", ["Clear selection"] = "清除选择", ["Pay quantity"] = "付款数量",
        ["Select what this person is paying for. Paid quantities are removed for the next person."] = "选择此人支付的品项，已付款数量不会显示给下一位付款人。", ["This person's subtotal"] = "此人应付小计",
        ["Record only a payment you have received."] = "请确认收到款项后才记录付款。", ["Payment method"] = "付款方式", ["Cash received (RM)"] = "实收现金（RM）", ["Change:"] = "找零：",
        ["Payment reference (optional)"] = "付款备注（选填）", ["Confirm payment & close order"] = "确认付款并结单", ["Confirm payment & next person"] = "确认付款，下一位", ["Back to order"] = "返回编辑订单",
        ["Recording payment..."] = "正在记录付款…", ["Retry payment confirmation"] = "重试确认付款", ["Confirm close order"] = "确认结单", ["No payment is due. Confirm to close this order."] = "此订单无需付款，请确认结单。",
        ["Cash received must cover this person's subtotal."] = "实收现金不可少于此人应付小计。", ["Total"] = "合计", ["Paid"] = "已付", ["Balance"] = "余额", ["Qty"] = "数量", ["Unit RM"] = "单价 RM", ["Total RM"] = "合计 RM",
        ["Continue an order"] = "继续订单", ["Add Item"] = "添加品项", ["Cancel Item"] = "取消品项", ["Your order"] = "当前订单", ["Checkout"] = "付款", ["Continue payment"] = "继续付款", ["Quantity"] = "数量",
        ["Cancel quantity"] = "取消数量", ["Remove"] = "移除", ["Kitchen notes"] = "厨房备注", ["Order notes (optional)"] = "订单备注（选填）", ["Cancellation reason (optional)"] = "取消原因（选填）",
        ["Send to kitchen"] = "发送到厨房", ["Add to order"] = "加入订单", ["Variation"] = "选项", ["MAKE IT YOURS"] = "自定义餐点", ["New items total"] = "新增品项合计",
        ["Order type"] = "用餐方式", ["Select food"] = "选择餐点", ["Select a table"] = "选择桌位", ["Restaurant tables"] = "餐厅桌位", ["Search food or drinks"] = "搜索餐点或饮料",
        ["Choose a variation, quantity, and any kitchen notes."] = "选择规格、数量并填写厨房备注。", ["Pack it up. Choose food and send straight to the kitchen."] = "选择餐点，直接发送到厨房。",
        ["Settle in. Choose a table before adding food."] = "先选择桌位，再添加餐点。", ["Choose food and add another kitchen ticket."] = "选择餐点并发送新的厨房单。", ["Select items and quantities to cancel."] = "选择要取消的品项和数量。",
        ["Available"] = "空桌", ["Occupied"] = "使用中", ["Choose variation +"] = "选择规格 +", ["No outstanding orders."] = "目前没有待处理订单。", ["Loading kitchen tickets…"] = "正在载入订单…",
        ["New"] = "新", ["Preparing"] = "正在准备", ["Ready"] = "完成", ["Cancelled"] = "已取消", ["Order notes:"] = "订单备注：", ["Start preparing"] = "正在准备", ["Mark ready"] = "完成",
        ["YOUR DAILY WORKSPACE"] = "每日工作区", ["Ready for service."] = "准备开工。", ["Good food. Smooth service. Everything in one place."] = "美食与服务，一站管理。",
        ["Restaurant POS"] = "餐厅收银系统", ["Local restaurant workspace"] = "本地餐厅工作区", ["FROM TABLE TO KITCHEN"] = "从餐桌到厨房", ["A little less rush."] = "少一点忙乱。", ["A lot more flow."] = "多一份顺畅。",
        ["Start a new order"] = "新建订单", ["Choose your workspace"] = "选择工作区", ["One service, working together"] = "齐心协力，服务每桌", ["Take an order"] = "开始点餐", ["Keep things cooking"] = "管理厨房订单", ["Bring up the bill"] = "查看账单",
        ["Open ordering"] = "进入点餐", ["Open kitchen"] = "进入厨房", ["Open cashier"] = "进入收银", ["Made for your restaurant"] = "为您的餐厅而设"
    };
}

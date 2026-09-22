using System.Text.Json.Nodes;

namespace Baker.Core;

/// <summary>
/// 一句文案：文案表的键 + 参数。谁产生这句话，谁就把键和参数带着走；程序只看键，文案只在输出时渲染。
/// 取代原来"先渲染成英文、登记进进程级表、之后再按英文反查"的 Emit/Localize。
/// <see cref="Args"/> 是英文（plan/bake v3 的英文字段与 en）参数；嵌进句子里的那半句本身分语言时另给
/// <see cref="ZhArgs"/>，为 null 表示两种语言共用一套。
/// </summary>
public sealed record Message(string Key, object?[] Args, object?[]? ZhArgs = null)
{
    public Message(string key) : this(key, []) { }

    /// <summary>v3 英文原文（写进 reason / detail / 异常消息）。</summary>
    public string Text => MessageCatalog.RenderLegacy(Key, Args);

    /// <summary>{key, zh, en, params}，写进 *_localized 字段。</summary>
    public JsonObject Localized() => MessageCatalog.Localized(Key, ZhArgs ?? Args, Args);

    /// <summary>写成 target[field]（英文原文）与 target[field + "_localized"]（{key, zh, en, params}）一对，返回 target。</summary>
    public JsonObject Write(JsonObject target, string field)
    {
        target[field] = Text;
        target[field + "_localized"] = Localized();
        return target;
    }

    /// <summary>按语言取这一句。</summary>
    public string In(string language) =>
        MessageCatalog.Get(Key, language, MessageCatalog.NormalizeLanguage(language) == MessageCatalog.Chinese ? ZhArgs ?? Args : Args);

    /// <summary>造一个带着这句话的异常：消息仍是英文原文，捕获方用 <see cref="Of"/> 取回键与参数，不拿异常文本反查。</summary>
    public TError Error<TError>(Func<string, TError> create) where TError : Exception
    {
        TError error = create(Text);
        error.Data[nameof(Message)] = this;
        return error;
    }

    public static Message? Of(Exception error) => error.Data[nameof(Message)] as Message;
}

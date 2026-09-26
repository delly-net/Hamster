namespace Hamster.Api.Events;

/// <summary>
/// 一次事件派发的结果统计。
/// </summary>
/// <param name="HandlerCount">本次派发中被调用的订阅者数量（可能为 0）。</param>
/// <param name="FailureCount">其中抛出异常的订阅者数量。</param>
/// <remarks>
/// 派发结果以**返回值**而非异常传递给调用方：调用方（如结算执行任务）要据此决定
/// 「这次结算是否算执行成功、要不要标记已执行」，那是一份**数据**，
/// 而不是要靠异常来传递的控制流。订阅者自身的异常已在总线内被逐个记录到日志。
/// <para>
/// <see cref="FailureCount"/> 为 0 有两种情形：全部订阅者成功，或**一个订阅者都没有**。
/// 两者在调用方眼里同义——「没人订阅」不等于「执行失败」。
/// </para>
/// </remarks>
public readonly record struct EventDispatchResult(int HandlerCount, int FailureCount)
{
    /// <summary>本次派发是否无失败（含无订阅者的情形）。</summary>
    public bool IsSuccessful => FailureCount == 0;
}

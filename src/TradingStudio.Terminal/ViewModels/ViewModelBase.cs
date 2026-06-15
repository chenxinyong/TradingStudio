using CommunityToolkit.Mvvm.ComponentModel;

namespace TradingStudio.Terminal.ViewModels;

/// <summary>
/// ViewModel 基类 — 导航生命周期 + Dispatcher 引用。
/// 所有 ViewModel 继承此类。
/// </summary>
public abstract partial class ViewModelBase : ObservableObject
{
    public bool IsActive { get; private set; }

    /// <summary>导航到本 View 时调用（启动订阅、Timer 等）</summary>
    public virtual void OnNavigatedTo() => IsActive = true;

    /// <summary>离开本 View 时调用（停止订阅、Timer 等）</summary>
    public virtual void OnNavigatedFrom() => IsActive = false;
}

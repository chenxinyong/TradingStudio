using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TradingStudio.Terminal.Core.Messaging;

namespace TradingStudio.Terminal.Panels;

/// <summary>
/// Manages side-bar and bottom-panel lifecycle: register, show, hide, toggle.
/// Panels are lazily created on first access.
/// </summary>
public class PanelManager
{
    private readonly Dictionary<string, PanelDescriptor> _panels = new();
    private readonly Dictionary<string, Lazy<object>> _viewModels = new();
    private readonly EventBus? _eventBus;

    public PanelManager(EventBus? eventBus = null)
    {
        _eventBus = eventBus;
    }

    public PanelDescriptor Register(string id, string title, PanelLocation location,
        Func<object> viewModelFactory, int order = 0)
    {
        var descriptor = new PanelDescriptor(id, title, location, order);
        _panels[id] = descriptor;
        _viewModels[id] = new Lazy<object>(viewModelFactory);
        _eventBus?.Publish(new PanelRegistered(descriptor));
        return descriptor;
    }

    public PanelDescriptor? Get(string id) => _panels.TryGetValue(id, out var d) ? d : null;

    public object? GetViewModel(string id) =>
        _viewModels.TryGetValue(id, out var lazy) ? lazy.Value : null;

    public IEnumerable<PanelDescriptor> All => _panels.Values.OrderBy(p => p.Order);

    public void Show(string id)
    {
        var descriptor = Get(id);
        if (descriptor == null) return;
        descriptor.IsVisible = true;
        _eventBus?.Publish(new PanelVisibilityChanged(descriptor, true));
    }

    public void Hide(string id)
    {
        var descriptor = Get(id);
        if (descriptor == null) return;
        descriptor.IsVisible = false;
        _eventBus?.Publish(new PanelVisibilityChanged(descriptor, false));
    }

    public void Toggle(string id)
    {
        var descriptor = Get(id);
        if (descriptor == null) return;
        if (descriptor.IsVisible) Hide(id); else Show(id);
    }
}

public class PanelDescriptor
{
    public string Id { get; }
    public string Title { get; }
    public PanelLocation Location { get; }
    public int Order { get; }
    public bool IsVisible { get; set; }

    public PanelDescriptor(string id, string title, PanelLocation location, int order)
    {
        Id = id; Title = title; Location = location; Order = order;
    }
}

public enum PanelLocation { Sidebar, Bottom, Right }

public record PanelRegistered(PanelDescriptor Panel);
public record PanelVisibilityChanged(PanelDescriptor Panel, bool IsVisible);

#if COMBOBULATE_NO_XAML
using System;
using System.Collections.Generic;

namespace Combobulate;

/// <summary>XAML-free stand-ins so the existing DependencyProperty block in
/// Combobulate.cs compiles unchanged when built without XAML.</summary>
public delegate void PropertyChangedCallback(object d, DependencyPropertyChangedEventArgs e);

public readonly struct DependencyPropertyChangedEventArgs
{
    public object? OldValue { get; init; }
    public object? NewValue { get; init; }
}

public sealed class PropertyMetadata
{
    public object? DefaultValue { get; }
    public PropertyChangedCallback? Callback { get; }
    public PropertyMetadata(object? defaultValue) { DefaultValue = defaultValue; }
    public PropertyMetadata(object? defaultValue, PropertyChangedCallback? callback)
    { DefaultValue = defaultValue; Callback = callback; }
}

public sealed class DependencyProperty
{
    public string Name = string.Empty;
    public object? DefaultValue;
    public PropertyChangedCallback? Callback;
    public static DependencyProperty Register(string name, Type propertyType, Type ownerType, PropertyMetadata metadata)
        => new() { Name = name, DefaultValue = metadata.DefaultValue, Callback = metadata.Callback };
}

/// <summary>Minimal DependencyObject replacement backing GetValue/SetValue with a dictionary.</summary>
public abstract class DependencyObjectBase
{
    private readonly Dictionary<DependencyProperty, object?> _values = new();
    protected object? GetValue(DependencyProperty p) => _values.TryGetValue(p, out var v) ? v : p.DefaultValue;
    protected void SetValue(DependencyProperty p, object? value)
    {
        var old = GetValue(p);
        _values[p] = value;
        if (!Equals(old, value))
            p.Callback?.Invoke(this, new DependencyPropertyChangedEventArgs { OldValue = old, NewValue = value });
    }
}
#endif

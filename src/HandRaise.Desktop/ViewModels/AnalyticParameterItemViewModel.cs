using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using HandRaise.Domain.Analytics;

namespace HandRaise.Desktop.ViewModels;

public sealed class AnalyticParameterItemViewModel : ObservableObject
{
    private readonly ParameterDefinition _definition;
    private double _numberValue;
    private bool _boolValue;
    private string _stringValue = string.Empty;
    private string _selectedOptionValue = string.Empty;

    public AnalyticParameterItemViewModel(ParameterDefinition definition)
    {
        _definition = definition;
        Key = definition.Key;
        Label = definition.Label;
        Description = definition.Description;
        Type = definition.Type;
        Min = definition.Min ?? 0;
        Max = definition.Max ?? 100;
        Step = definition.Step ?? 1;
        Options = definition.Options ?? [];

        if (definition.Type == ParameterType.Boolean)
        {
            _boolValue = definition.DefaultValue is bool b ? b : (definition.DefaultValue is JsonElement je && je.ValueKind == JsonValueKind.True);
        }
        else if (definition.Type == ParameterType.Number)
        {
            if (definition.DefaultValue is double d) _numberValue = d;
            else if (definition.DefaultValue is int i) _numberValue = i;
            else if (definition.DefaultValue is float f) _numberValue = f;
            else if (definition.DefaultValue is long l) _numberValue = l;
            else if (definition.DefaultValue is decimal dec) _numberValue = (double)dec;
            else if (definition.DefaultValue is JsonElement je && je.TryGetDouble(out var jd)) _numberValue = jd;
            else _numberValue = Min;
        }
        else if (definition.Type == ParameterType.Select)
        {
            _selectedOptionValue = definition.DefaultValue?.ToString() ?? (Options.Count > 0 ? Options[0].Value : string.Empty);
        }
        else
        {
            _stringValue = definition.DefaultValue?.ToString() ?? string.Empty;
        }
    }

    public string Key { get; }
    public string Label { get; }
    public string? Description { get; }
    public ParameterType Type { get; }
    public double Min { get; }
    public double Max { get; }
    public double Step { get; }
    public IReadOnlyList<ParameterOption> Options { get; }

    public bool IsBoolean => Type == ParameterType.Boolean;
    public bool IsNumber => Type == ParameterType.Number;
    public bool IsSelect => Type == ParameterType.Select;
    public bool IsString => Type == ParameterType.String;

    public bool IsPercentage => IsNumber && Max <= 1.0 && Min >= 0;

    public bool BoolValue
    {
        get => _boolValue;
        set => SetProperty(ref _boolValue, value);
    }

    public double NumberValue
    {
        get => _numberValue;
        set
        {
            if (SetProperty(ref _numberValue, value))
            {
                OnPropertyChanged(nameof(FormattedNumberValue));
                OnPropertyChanged(nameof(IntNumberValue));
            }
        }
    }

    public int IntNumberValue
    {
        get => (int)Math.Round(_numberValue);
        set => NumberValue = value;
    }

    public string FormattedNumberValue => IsPercentage
        ? $"{_numberValue:P0}"
        : Step < 1
            ? $"{_numberValue:F2}"
            : $"{_numberValue:F0}";

    public string StringValue
    {
        get => _stringValue;
        set => SetProperty(ref _stringValue, value);
    }

    public string SelectedOptionValue
    {
        get => _selectedOptionValue;
        set => SetProperty(ref _selectedOptionValue, value);
    }

    public object? GetTypedValue()
    {
        return Type switch
        {
            ParameterType.Boolean => _boolValue,
            ParameterType.Number => Step >= 1 && _definition.DefaultValue is int or long
                ? (int)Math.Round(_numberValue)
                : _numberValue,
            ParameterType.Select => _selectedOptionValue,
            _ => _stringValue
        };
    }
}

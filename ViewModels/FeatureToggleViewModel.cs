using ANEVRED.Models;

namespace ANEVRED.ViewModels;

public sealed class FeatureToggleViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private readonly Action _changed;

    public FeatureToggleViewModel(FeatureDefinition definition, string title, AppSettings settings, Action changed)
    {
        Definition = definition;
        Title = title;
        _settings = settings;
        _changed = changed;
    }

    public FeatureDefinition Definition { get; }
    public string Title { get; }

    public bool IsEnabled
    {
        get => Definition.SettingsPropertyName is null
            || _settings.GetFeatureEnabled(Definition.SettingsPropertyName);
        set
        {
            if (Definition.SettingsPropertyName is null
                || _settings.GetFeatureEnabled(Definition.SettingsPropertyName) == value)
            {
                return;
            }

            _settings.SetFeatureEnabled(Definition.SettingsPropertyName, value);
            OnPropertyChanged();
            _changed();
        }
    }
}

using Avalonia.Data.Converters;
using System.Globalization;

namespace ShareX.Tools;

public sealed class LocalizedDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            AIProvider.OpenAI => Localization.Strings.LocalizedDisplayConverter_OpenAI,
            AIProvider.Gemini => Localization.Strings.LocalizedDisplayConverter_Gemini,
            AIProvider.OpenRouter => Localization.Strings.LocalizedDisplayConverter_OpenRouter,
            AIProvider.OpenAILegacy => Localization.Strings.LocalizedDisplayConverter_OpenAI_legacy,
            BackgroundRemovalDevice.Auto => Localization.Strings.LocalizedDisplayConverter_Automatic,
            BackgroundRemovalDevice.GPU => Localization.Strings.LocalizedDisplayConverter_GPU,
            BackgroundRemovalDevice.CPU => Localization.Strings.LocalizedDisplayConverter_CPU,
            "minimal" => Localization.Strings.LocalizedDisplayConverter_Minimal,
            "low" => Localization.Strings.LocalizedDisplayConverter_Low,
            "medium" => Localization.Strings.LocalizedDisplayConverter_Medium,
            "high" => Localization.Strings.LocalizedDisplayConverter_High,
            null => string.Empty,
            _ => value.ToString() ?? string.Empty
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

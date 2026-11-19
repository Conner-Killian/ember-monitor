namespace Ember.Services;

/// <summary>Stand-in for the app's Temps (which reads the running app's settings): plain °C numbers.</summary>
public static class Temps
{
    public static string Number(double? c) => c is double v ? $"{Math.Round(v, MidpointRounding.AwayFromZero):0}" : "—";
}

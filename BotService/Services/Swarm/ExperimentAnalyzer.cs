using System.Text.Json;

namespace BotService.Services.Swarm;

/// <summary>
/// Computes statistical analysis for A/B experiment results.
/// Simple Welch's t-test for comparing two sample means.
/// </summary>
public static class ExperimentAnalyzer
{
    /// <summary>Analyze experiment results from metrics JSON.</summary>
    public static ExperimentAnalysis Analyze(string? metricsJson)
    {
        if (string.IsNullOrWhiteSpace(metricsJson))
            return new ExperimentAnalysis { Summary = "No metrics data" };

        try
        {
            using var doc = JsonDocument.Parse(metricsJson);
            var root = doc.RootElement;

            var ga = root.GetProperty("groupA");
            var gb = root.GetProperty("groupB");

            var aInteractions = ga.GetProperty("interactions").GetInt32();
            var aSuccesses = ga.GetProperty("successes").GetInt32();
            var aRate = aInteractions > 0 ? (double)aSuccesses / aInteractions : 0;

            var bInteractions = gb.GetProperty("interactions").GetInt32();
            var bSuccesses = gb.GetProperty("successes").GetInt32();
            var bRate = bInteractions > 0 ? (double)bSuccesses / bInteractions : 0;

            var totalN = aInteractions + bInteractions;
            var sampleAdequate = totalN >= 30;

            // Welch's t-test for two proportions
            var tStat = ComputeTStatistic(aRate, bRate, aInteractions, bInteractions);
            var df = ComputeDegreesOfFreedom(aRate, bRate, aInteractions, bInteractions);
            var pValue = ApproximatePValue(Math.Abs(tStat), df);

            var significant = pValue < 0.05;
            var winner = !significant ? "none" :
                aRate > bRate ? "A" : "B";

            return new ExperimentAnalysis
            {
                GroupARate = aRate,
                GroupBRate = bRate,
                GroupACount = aInteractions,
                GroupBCount = bInteractions,
                TStatistic = tStat,
                DegreesOfFreedom = df,
                PValue = pValue,
                IsSignificant = significant,
                SampleSizeAdequate = sampleAdequate,
                Winner = winner,
                Summary = significant
                    ? $"Group {winner} wins ({(winner == "A" ? aRate : bRate):P1} vs {(winner == "A" ? bRate : aRate):P1}, p={pValue:F4})"
                    : $"No significant difference (A={aRate:P1}, B={bRate:P1}, p={pValue:F4})"
            };
        }
        catch (Exception ex)
        {
            return new ExperimentAnalysis { Summary = $"Analysis error: {ex.Message}" };
        }
    }

    /// <summary>
    /// Welch's t-test for two proportions.
    /// Uses pooled proportion under H₀ for standard error.
    /// </summary>
    internal static double ComputeTStatistic(double p1, double p2, int n1, int n2)
    {
        if (n1 == 0 || n2 == 0) return 0;

        var pooledP = (p1 * n1 + p2 * n2) / (n1 + n2);
        var se = Math.Sqrt(pooledP * (1 - pooledP) * (1.0 / n1 + 1.0 / n2));

        return se == 0 ? 0 : (p1 - p2) / se;
    }

    /// <summary>
    /// Approximate degrees of freedom using Welch-Satterthwaite equation.
    /// </summary>
    internal static double ComputeDegreesOfFreedom(double p1, double p2, int n1, int n2)
    {
        if (n1 <= 1 || n2 <= 1) return 1;

        var v1 = p1 * (1 - p1) / n1;
        var v2 = p2 * (1 - p2) / n2;
        var numerator = (v1 + v2) * (v1 + v2);
        var denominator = v1 * v1 / (n1 - 1) + v2 * v2 / (n2 - 1);

        return denominator == 0 ? n1 + n2 - 2 : numerator / denominator;
    }

    /// <summary>
    /// Approximate two-tailed p-value using normal distribution approximation.
    /// For df > 30, t-distribution approximates normal. For smaller df, 
    /// this is a rough approximation sufficient for experiment comparison.
    /// </summary>
    internal static double ApproximatePValue(double absT, double df)
    {
        if (df <= 0 || double.IsNaN(absT)) return 1.0;
        // Normal CDF approximation for two-tailed test
        var z = absT;
        var p = 2.0 * (1.0 - NormalCdf(z));
        return Math.Max(0, Math.Min(1, p));
    }

    /// <summary>Standard normal CDF approximation (Abramowitz and Stegun).</summary>
    internal static double NormalCdf(double x)
    {
        if (x < -8) return 0;
        if (x > 8) return 1;

        const double a1 = 0.254829592;
        const double a2 = -0.284496736;
        const double a3 = 1.421413741;
        const double a4 = -1.453152027;
        const double a5 = 1.061405429;
        const double p = 0.3275911;

        var sign = x < 0 ? -1 : 1;
        x = Math.Abs(x) / Math.Sqrt(2);
        var t = 1.0 / (1.0 + p * x);
        var y = 1.0 - ((((a5 * t + a4) * t + a3) * t + a2) * t + a1) * t * Math.Exp(-x * x);

        return 0.5 * (1.0 + sign * y);
    }
}

public class ExperimentAnalysis
{
    public double GroupARate { get; set; }
    public double GroupBRate { get; set; }
    public int GroupACount { get; set; }
    public int GroupBCount { get; set; }
    public double TStatistic { get; set; }
    public double DegreesOfFreedom { get; set; }
    public double PValue { get; set; }
    public bool IsSignificant { get; set; }
    public bool SampleSizeAdequate { get; set; }
    public string Winner { get; set; } = "none";
    public string Summary { get; set; } = "";
}

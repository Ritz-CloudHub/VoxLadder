using System.Collections.Generic;

namespace QX.BackTesting.Strategies.QuantumVaR
{
    public struct ShockResult
    {
        public double ShockPct;
        public double ScenarioSpot;
        public double PortfolioPnL;
        public double PnLAsMarginPct;
    }

    public class VaRResult
    {
        // Per-shock PnL lookup
        public Dictionary<double, double> PnLAtShock { get; set; } = new Dictionary<double, double>();

        /// <summary>
        /// Per-shock PnL under correlated VIX scenario. Key = shock percentage.
        /// </summary>
        public Dictionary<double, double> PnLAtShock_CorrVix { get; set; } = new Dictionary<double, double>();

        /// <summary>
        /// Per-shock PnL under flat (unchanged) VIX scenario. Key = shock percentage.
        /// </summary>
        public Dictionary<double, double> PnLAtShock_FlatVix { get; set; } = new Dictionary<double, double>();

        public double TargetVaR { get; set; }

        // Overall
        public double WorstPnL { get; set; }
        public double WorstShock { get; set; }
        public bool NeedsHedge { get; set; }

        // Downside (negative shocks)
        public double DownsideWorstPnL { get; set; }
        public double DownsideWorstShockPct { get; set; }
        public bool DownsideNeedsHedge { get; set; }

        // Upside (positive shocks)
        public double UpsideWorstPnL { get; set; }
        public double UpsideWorstShockPct { get; set; }
        public bool UpsideNeedsHedge { get; set; }

        // Full stress table
        public ShockResult[] AllShockResults { get; set; }
    }
}

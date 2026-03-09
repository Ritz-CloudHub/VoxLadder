using QX.FinLib.Common;
using QX.FinLib.Instrument;
using QX.FinLib.TSOptions;
using QX.BackTesting.Strategies;
using System;
using System.Collections.Generic;
using System.Linq;

namespace QX.BackTesting.Strategies.QuantumVaR
{
    /// <summary>
    /// Standalone overnight VaR calculator using Black-Scholes repricing.
    /// Computes portfolio VaR under spot shock + dual VIX scenarios (correlated and flat).
    ///
    /// <para><b>Usage example:</b></para>
    /// <code>
    /// var bsCalc = new BlackScholesCalculator(...);
    /// var calc = new OvernightVaRCalculator(lotSize: 75, bsCalc: bsCalc);
    ///
    /// double targetVaR = -1.0 * 0.03 * totalMargin; // 3% of margin
    /// double[] shocks = { -0.10, -0.05, -0.03, -0.02, 0.02, 0.03, 0.05, 0.10 };
    /// var vixCorr = new Dictionary&lt;double, double&gt;
    /// {
    ///     { -0.10, +0.70 }, { -0.05, +0.40 }, { -0.03, +0.25 }, { -0.02, +0.15 },
    ///     { +0.02, -0.10 }, { +0.03, -0.15 }, { +0.05, -0.20 }, { +0.10, -0.30 }
    /// };
    ///
    /// VaRResult result = calc.ComputeVaR(positions, spot, targetVaR, shocks, vixCorr,
    ///     accessor, currentVix, currentDT, totalMargin);
    ///
    /// if (result.NeedsHedge)
    /// {
    ///     // Find hedge candidates, then:
    ///     VaRResult postHedge = calc.ComputePostHedgeVaR(result, spot, targetVaR,
    ///         shocks, vixCorr, putHedge, callHedge, currentVix, currentDT, totalMargin);
    /// }
    /// </code>
    /// </summary>
    public class OvernightVaRCalculator
    {
        protected readonly int _lotSize;
        protected readonly BlackScholesCalculator _bsCalc;

        public OvernightVaRCalculator(int lotSize, BlackScholesCalculator bsCalc)
        {
            _lotSize = lotSize;
            _bsCalc = bsCalc;
        }

        /// <summary>
        /// Compute BS-repriced VaR with dual VIX scenarios.
        /// For each shock level, tests two VIX scenarios (correlated + unchanged) and takes worst PnL.
        /// Each position is repriced using GetOptionBlackScholesPriceAtDateX at shocked spot/VIX/next-morning TTE.
        /// PnL = (tomorrowBSPrice - todayLivePrice) x NetLots x LotSize
        /// </summary>
        /// <param name="positions">Current portfolio positions with signed NetLots.</param>
        /// <param name="spot">Current spot price of the underlying.</param>
        /// <param name="targetVaR">Maximum acceptable overnight loss (negative number, e.g. -115200).</param>
        /// <param name="shockLevels">Spot shock scenarios as decimals (e.g. -0.10 for -10%).</param>
        /// <param name="vixCorrelation">Spot shock to correlated VIX change mapping.</param>
        /// <param name="accessor">Option data accessor for resolving instruments and live prices.</param>
        /// <param name="currentVix">Current India VIX value.</param>
        /// <param name="currentDT">Current date/time (IST) for TTE calculation.</param>
        /// <param name="totalMargin">Total margin for PnL-as-margin-pct calculation (optional).</param>
        public VaRResult ComputeVaR(
            List<PortfolioPosition> positions,
            double spot,
            double targetVaR,
            double[] shockLevels,
            Dictionary<double, double> vixCorrelation,
            IOptionCommandAccessor accessor,
            double currentVix,
            DateTime currentDT,
            double totalMargin = 0)
        {
            var result = new VaRResult { TargetVaR = targetVaR };

            if (positions == null || positions.Count == 0)
            {
                result.NeedsHedge = false;
                result.WorstPnL = 0;
                result.AllShockResults = new ShockResult[0];
                return result;
            }

            // Step 1: Resolve OptionsInstruments and get today's live prices
            var resolvedPositions = new List<(PortfolioPosition pos, OptionsInstrument option, double todayPrice)>();

            foreach (var pos in positions)
            {
                // Skip positions expiring today — they settle at 15:29, no overnight risk
                if (pos.Expiry.Date <= currentDT.Date)
                    continue;

                OptionsInstrument option;
                try
                {
                    option = accessor.GetOptionFromStrikePrice(pos.Type, pos.Strike, pos.Expiry);
                }
                catch (Exception)
                {
                    continue; // API fetch failed for this option — skip
                }
                if (option == null) continue;

                double todayPrice = accessor.GetOptionPremium(option);
                if (double.IsNaN(todayPrice))
                {
                    // Fallback: use BS price at current spot/VIX
                    todayPrice = _bsCalc.GetOptionBlackScholesPrice(option, spot, currentDT, indiaVix: currentVix);
                    if (double.IsNaN(todayPrice)) continue;
                }

                resolvedPositions.Add((pos, option, todayPrice));
            }

            if (resolvedPositions.Count == 0)
            {
                result.NeedsHedge = false;
                result.WorstPnL = 0;
                result.AllShockResults = new ShockResult[0];
                return result;
            }

            // Step 2: Valuation datetime = next trading morning 09:20
            DateTime valuationDT = currentDT.Date.AddDays(1).Add(new TimeSpan(09, 20, 0));

            // Step 3: Compute PnL at each shock level (dual VIX scenarios)
            double overallWorstPnL = double.MaxValue;
            double overallWorstShock = 0;

            double downsideWorstPnL = double.MaxValue;
            double downsideWorstShock = 0;
            bool downsideBreach = false;

            double upsideWorstPnL = double.MaxValue;
            double upsideWorstShock = 0;
            bool upsideBreach = false;

            var allShocks = new ShockResult[shockLevels.Length];

            for (int s = 0; s < shockLevels.Length; s++)
            {
                double shock = shockLevels[s];
                double scenarioSpot = spot * (1.0 + shock);

                // Get correlated VIX change
                double vixChangePct = 0;
                if (vixCorrelation != null && vixCorrelation.ContainsKey(shock))
                    vixChangePct = vixCorrelation[shock];

                // Scenario A: Correlated VIX
                double shockedVixA = currentVix * (1.0 + vixChangePct);
                double pnlA = ComputePortfolioPnLForScenario(resolvedPositions, scenarioSpot, shockedVixA, valuationDT);

                // Scenario B: VIX unchanged
                double pnlB = ComputePortfolioPnLForScenario(resolvedPositions, scenarioSpot, currentVix, valuationDT);

                // Take the WORSE (lower) PnL
                double totalPnL = Math.Min(pnlA, pnlB);

                result.PnLAtShock[shock] = totalPnL;
                result.PnLAtShock_CorrVix[shock] = pnlA;
                result.PnLAtShock_FlatVix[shock] = pnlB;

                double marginPct = totalMargin != 0 ? totalPnL / totalMargin : 0;
                allShocks[s] = new ShockResult
                {
                    ShockPct = shock,
                    ScenarioSpot = scenarioSpot,
                    PortfolioPnL = totalPnL,
                    PnLAsMarginPct = marginPct
                };

                // Overall worst
                if (totalPnL < overallWorstPnL)
                {
                    overallWorstPnL = totalPnL;
                    overallWorstShock = shock;
                }

                // Per-side tracking
                if (shock < 0)
                {
                    if (totalPnL < downsideWorstPnL)
                    {
                        downsideWorstPnL = totalPnL;
                        downsideWorstShock = shock;
                    }
                    if (totalPnL < targetVaR)
                        downsideBreach = true;
                }
                else if (shock > 0)
                {
                    if (totalPnL < upsideWorstPnL)
                    {
                        upsideWorstPnL = totalPnL;
                        upsideWorstShock = shock;
                    }
                    if (totalPnL < targetVaR)
                        upsideBreach = true;
                }
            }

            result.AllShockResults = allShocks;
            result.WorstPnL = overallWorstPnL;
            result.WorstShock = overallWorstShock;
            result.NeedsHedge = overallWorstPnL < targetVaR;

            result.DownsideWorstPnL = downsideWorstPnL == double.MaxValue ? 0 : downsideWorstPnL;
            result.DownsideWorstShockPct = downsideWorstShock;
            result.DownsideNeedsHedge = downsideBreach;

            result.UpsideWorstPnL = upsideWorstPnL == double.MaxValue ? 0 : upsideWorstPnL;
            result.UpsideWorstShockPct = upsideWorstShock;
            result.UpsideNeedsHedge = upsideBreach;

            return result;
        }

        /// <summary>
        /// BS-reprice the portfolio at a given scenario (shocked spot + shocked VIX).
        /// PnL = sum of (tomorrowBSPrice - todayLivePrice) x NetLots x LotSize for each position.
        /// </summary>
        protected double ComputePortfolioPnLForScenario(
            List<(PortfolioPosition pos, OptionsInstrument option, double todayPrice)> resolvedPositions,
            double scenarioSpot,
            double scenarioVix,
            DateTime valuationDT)
        {
            double totalPnL = 0;

            foreach (var (pos, option, todayPrice) in resolvedPositions)
            {
                double tomorrowPrice = _bsCalc.GetOptionBlackScholesPriceAtDateX(
                    option, scenarioSpot, valuationDT, indiaVix: scenarioVix);

                if (double.IsNaN(tomorrowPrice))
                {
                    // Fallback to intrinsic if BS fails (safety net, should be rare)
                    if (pos.Type == OptionType.CE)
                        tomorrowPrice = Math.Max(scenarioSpot - pos.Strike, 0.05);
                    else
                        tomorrowPrice = Math.Max(pos.Strike - scenarioSpot, 0.05);
                }

                double pnl = pos.NetLots * (tomorrowPrice - todayPrice) * _lotSize;
                totalPnL += pnl;
            }

            return totalPnL;
        }

        /// <summary>
        /// Recompute VaR with hedge positions added, using BS repricing for hedge legs.
        /// Dual VIX scenarios: for each shock, take the worst of (correlated VIX, unchanged VIX).
        /// Hedge PnL = Lots x LotSize x (tomorrowBSPrice - entryPremium)
        /// </summary>
        public VaRResult ComputePostHedgeVaR(
            VaRResult preHedgeVaR,
            double spot,
            double targetVaR,
            double[] shockLevels,
            Dictionary<double, double> vixCorrelation,
            HedgeCandidate putHedge,
            HedgeCandidate callHedge,
            double currentVix,
            DateTime currentDT,
            double totalMargin = 0)
        {
            var result = new VaRResult { TargetVaR = targetVaR };

            DateTime valuationDT = currentDT.Date.AddDays(1).Add(new TimeSpan(09, 20, 0));

            double overallWorstPnL = double.MaxValue;
            double overallWorstShock = 0;
            double downsideWorstPnL = double.MaxValue;
            double downsideWorstShock = 0;
            bool downsideBreach = false;
            double upsideWorstPnL = double.MaxValue;
            double upsideWorstShock = 0;
            bool upsideBreach = false;

            var allShocks = new ShockResult[shockLevels.Length];

            for (int s = 0; s < shockLevels.Length; s++)
            {
                double shock = shockLevels[s];
                double scenarioSpot = spot * (1.0 + shock);
                double basePnL = preHedgeVaR.PnLAtShock[shock];

                // Get correlated VIX change
                double vixChangePct = 0;
                if (vixCorrelation != null && vixCorrelation.ContainsKey(shock))
                    vixChangePct = vixCorrelation[shock];

                // Scenario A: Correlated VIX — hedge PnL
                double shockedVixA = currentVix * (1.0 + vixChangePct);
                double hedgePnLA = ComputeHedgePnLForScenario(putHedge, callHedge, scenarioSpot, shockedVixA, valuationDT);

                // Scenario B: VIX unchanged — hedge PnL
                double hedgePnLB = ComputeHedgePnLForScenario(putHedge, callHedge, scenarioSpot, currentVix, valuationDT);

                // Take the scenario where hedge helps LEAST (worst combined PnL)
                double totalPnLA = basePnL + hedgePnLA;
                double totalPnLB = basePnL + hedgePnLB;
                double totalPnL = Math.Min(totalPnLA, totalPnLB);

                result.PnLAtShock[shock] = totalPnL;

                double marginPct = totalMargin != 0 ? totalPnL / totalMargin : 0;
                allShocks[s] = new ShockResult
                {
                    ShockPct = shock,
                    ScenarioSpot = scenarioSpot,
                    PortfolioPnL = totalPnL,
                    PnLAsMarginPct = marginPct
                };

                if (totalPnL < overallWorstPnL)
                {
                    overallWorstPnL = totalPnL;
                    overallWorstShock = shock;
                }

                if (shock < 0)
                {
                    if (totalPnL < downsideWorstPnL) { downsideWorstPnL = totalPnL; downsideWorstShock = shock; }
                    if (totalPnL < targetVaR) downsideBreach = true;
                }
                else if (shock > 0)
                {
                    if (totalPnL < upsideWorstPnL) { upsideWorstPnL = totalPnL; upsideWorstShock = shock; }
                    if (totalPnL < targetVaR) upsideBreach = true;
                }
            }

            result.AllShockResults = allShocks;
            result.WorstPnL = overallWorstPnL;
            result.WorstShock = overallWorstShock;
            result.NeedsHedge = overallWorstPnL < targetVaR;

            result.DownsideWorstPnL = downsideWorstPnL == double.MaxValue ? 0 : downsideWorstPnL;
            result.DownsideWorstShockPct = downsideWorstShock;
            result.DownsideNeedsHedge = downsideBreach;

            result.UpsideWorstPnL = upsideWorstPnL == double.MaxValue ? 0 : upsideWorstPnL;
            result.UpsideWorstShockPct = upsideWorstShock;
            result.UpsideNeedsHedge = upsideBreach;

            return result;
        }

        /// <summary>
        /// BS-reprice hedge legs at a given scenario.
        /// Hedge is always LONG, so PnL = Lots x LotSize x (tomorrowPrice - entryPremium)
        /// </summary>
        protected double ComputeHedgePnLForScenario(
            HedgeCandidate putHedge,
            HedgeCandidate callHedge,
            double scenarioSpot,
            double scenarioVix,
            DateTime valuationDT)
        {
            double hedgePnL = 0;

            if (putHedge != null)
            {
                double tomorrowPrice = _bsCalc.GetOptionBlackScholesPriceAtDateX(
                    putHedge.Option, scenarioSpot, valuationDT, indiaVix: scenarioVix);

                if (double.IsNaN(tomorrowPrice))
                    tomorrowPrice = Math.Max(putHedge.Strike - scenarioSpot, 0.05);

                hedgePnL += putHedge.Lots * _lotSize * (tomorrowPrice - putHedge.Premium);
            }

            if (callHedge != null)
            {
                double tomorrowPrice = _bsCalc.GetOptionBlackScholesPriceAtDateX(
                    callHedge.Option, scenarioSpot, valuationDT, indiaVix: scenarioVix);

                if (double.IsNaN(tomorrowPrice))
                    tomorrowPrice = Math.Max(scenarioSpot - callHedge.Strike, 0.05);

                hedgePnL += callHedge.Lots * _lotSize * (tomorrowPrice - callHedge.Premium);
            }

            return hedgePnL;
        }

        /// <summary>
        /// Create a scaled copy of a VaR result for fallback portfolio reduction.
        /// Scales PnL values by a factor (e.g. 0.8 for 20% reduction).
        /// </summary>
        public VaRResult CreateScaledVaR(VaRResult original, double scaleFactor,
            double spot, double targetVaR, double[] shockLevels)
        {
            var result = new VaRResult { TargetVaR = targetVaR };

            double overallWorstPnL = double.MaxValue;
            double overallWorstShock = 0;
            double downsideWorstPnL = double.MaxValue;
            double downsideWorstShock = 0;
            bool downsideBreach = false;
            double upsideWorstPnL = double.MaxValue;
            double upsideWorstShock = 0;
            bool upsideBreach = false;

            var allShocks = new ShockResult[shockLevels.Length];

            for (int s = 0; s < shockLevels.Length; s++)
            {
                double shock = shockLevels[s];
                double scaledPnL = original.PnLAtShock[shock] * scaleFactor;
                result.PnLAtShock[shock] = scaledPnL;

                allShocks[s] = new ShockResult
                {
                    ShockPct = shock,
                    ScenarioSpot = spot * (1.0 + shock),
                    PortfolioPnL = scaledPnL,
                    PnLAsMarginPct = 0
                };

                if (scaledPnL < overallWorstPnL) { overallWorstPnL = scaledPnL; overallWorstShock = shock; }

                if (shock < 0)
                {
                    if (scaledPnL < downsideWorstPnL) { downsideWorstPnL = scaledPnL; downsideWorstShock = shock; }
                    if (scaledPnL < targetVaR) downsideBreach = true;
                }
                else if (shock > 0)
                {
                    if (scaledPnL < upsideWorstPnL) { upsideWorstPnL = scaledPnL; upsideWorstShock = shock; }
                    if (scaledPnL < targetVaR) upsideBreach = true;
                }
            }

            result.AllShockResults = allShocks;
            result.WorstPnL = overallWorstPnL;
            result.WorstShock = overallWorstShock;
            result.NeedsHedge = overallWorstPnL < targetVaR;

            result.DownsideWorstPnL = downsideWorstPnL == double.MaxValue ? 0 : downsideWorstPnL;
            result.DownsideWorstShockPct = downsideWorstShock;
            result.DownsideNeedsHedge = downsideBreach;

            result.UpsideWorstPnL = upsideWorstPnL == double.MaxValue ? 0 : upsideWorstPnL;
            result.UpsideWorstShockPct = upsideWorstShock;
            result.UpsideNeedsHedge = upsideBreach;

            return result;
        }
    }
}

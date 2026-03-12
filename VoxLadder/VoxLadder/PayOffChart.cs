using MathNet.Numerics.Distributions;
using MathNet.Numerics.Integration;
using QX.Base.Common.InstrumentInfo;
using QX.FinLib.Common;
using QX.FinLib.Data;
using QX.FinLib.Data.TI;
using QX.FinLib.Instrument;
using QX.FinLib.TSOptions;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace QX.BackTesting.Strategies
{
    public enum BreakEvenType
    {
        BreakUp,
        BreakDown
    }

    public struct OptionsWithQuantity
    {
        public OptionsInstrument Option { get; }
        public double Quantity { get; }
        public OptionsWithQuantity(OptionsInstrument option, double quantity)
        {
            Option = option ?? throw new ArgumentNullException(nameof(option));
            Quantity = quantity;
        }
    }

    public struct LinearSegment
    {
        public double MinSpot { get; set; }
        public double MaxSpot { get; set; }
        public double Slope { get; set; }
        public double Intercept { get; set; }
    }

    public struct PayOffMetrics
    {
        public double MaxProfit { get; set; }
        public double MaxLoss { get; set; }        
        public double RiskReward { get; set; }
        public double POP { get; set; }
        public double POL { get; set; }
        public double PMP { get; set; }
        public double PML { get; set; }
        public double ExpectedValue { get; set; }
        public double EdgeFactor { get; set; }  
        public double RightMostPayoff { get; set; }
        public double LeftMostPayoff { get; set; }

        public bool HasNaN()
        {
            return double.IsNaN(MaxProfit) ||
                   double.IsNaN(MaxLoss) ||
                   double.IsNaN(RiskReward) ||
                   double.IsNaN(POP) ||
                   double.IsNaN(POL) ||
                   double.IsNaN(PMP) ||
                   double.IsNaN(PML) ||
                   double.IsNaN(ExpectedValue) ||
                   double.IsNaN(EdgeFactor);
        }

    }


    public class PayOffChartHelper
    {
        

        public IOptionCommandAccessor OptionCommandAccessor;


        public BlackScholesCalculator BlackScholesCalculator;

        public PayOffChartHelper(IOptionCommandAccessor optionCommandAccessor, BlackScholesCalculator blackScholesCalculator)
        {
            OptionCommandAccessor = optionCommandAccessor;
            BlackScholesCalculator = blackScholesCalculator;
        }



        #region PRIVATE HELPER METHODS
        private static double StableNormalCDF(double x)
        {
            if (x > 8) // Right tail approximation
            {
                double z = x * x;
                return 1.0 - Math.Exp(-z / 2) / (x * Math.Sqrt(2 * Math.PI)) * (1 - 1 / z);
            }
            if (x < -8) // Left tail approximation
            {
                double z = x * x;
                return Math.Exp(-z / 2) / (-x * Math.Sqrt(2 * Math.PI)) * (1 - 1 / z);
            }
            return Normal.CDF(0, 1, x);
        }
       
        private static double ComputeNormalProbOverIntervals(List<(double min, double max)> intervals, double mu, double std)
        {
            double prob = 0.0;
            foreach (var (min, max) in intervals)
            {
                double cdfMin = Normal.CDF(mu, std, min); 
                double cdfMax = double.IsPositiveInfinity(max) ? 1.0 : Normal.CDF(mu, std, max);
                prob += cdfMax - cdfMin;
            }
            return prob * 100; 
        }

        private static double ComputeLogNormalProbOverIntervals(List<(double min, double max)> intervals, double mu_log, double sigma)
        {
            double prob = 0.0;
            foreach (var (min, max) in intervals)
            {
                double cdfMin = (min <= 0) ? 0.0 : StableNormalCDF((Math.Log(min) - mu_log) / sigma);
                double cdfMax = (double.IsPositiveInfinity(max)) ? 1.0 : ((max <= 0) ? 0.0 : StableNormalCDF((Math.Log(max) - mu_log) / sigma));
                prob += cdfMax - cdfMin;
            }
            return prob * 100;
        }

        private static List<(double min, double max)> GetFlatIntervalsForValue(List<LinearSegment> linearSegments, double targetValue, bool useLogNormal)
        {
            const double tolerance = 1e-6; // Tolerance for floating-point comparison
            var intervals = new List<(double min, double max)>();
            double lowerClip = useLogNormal ? 1e-6 : 0.0;

            // Sort segments by MinSpot to ensure correct ordering
            var orderedSegments = linearSegments.OrderBy(s => s.MinSpot).ToList();

            foreach (var seg in orderedSegments)
            {
                if (seg.Slope == 0 && Math.Abs(seg.Intercept - targetValue) < tolerance)
                {
                    double min = Math.Max(seg.MinSpot, lowerClip); // Ensure appropriate lower bound
                    double max = seg.MaxSpot;
                    if (min < max)
                    {
                        intervals.Add((min, max));
                    }
                }
            }
            // Add left tail if flat and matches
            if (orderedSegments.Count > 0)
            {
                var left = orderedSegments[0];
                if (left.Slope == 0 && Math.Abs(left.Intercept - targetValue) < tolerance)
                {
                    double leftMin = double.NegativeInfinity;
                    double leftMax = left.MinSpot;
                    leftMin = Math.Max(leftMin, lowerClip);
                    if (leftMin < leftMax)
                    {
                        intervals.Insert(0, (leftMin, leftMax));
                    }
                }
            }
            // Add right tail if flat and matches
            if (orderedSegments.Count > 0)
            {
                var right = orderedSegments[orderedSegments.Count - 1];
                if (right.Slope == 0 && Math.Abs(right.Intercept - targetValue) < tolerance)
                {
                    double rightMin = right.MaxSpot;
                    double rightMax = double.PositiveInfinity;
                    if (rightMin < rightMax)
                    {
                        intervals.Add((rightMin, rightMax));
                    }
                }
            }
            return intervals;
        }

        private static List<(double min, double max)> GetPositivePayoffIntervals(List<LinearSegment> linearSegments, bool useLogNormal)
        {

            var intervals = new List<(double min, double max)>();
            var orderedSegments = linearSegments.OrderBy(s => s.MinSpot).ToList();

            foreach (var seg in orderedSegments)
            {
                double segMin = seg.MinSpot;
                double segMax = seg.MaxSpot;
                double slope = seg.Slope;
                double intercept = seg.Intercept;
                double start, end;

                if (slope == 0.0)
                {
                    if (intercept > 0.0)
                    {
                        start = useLogNormal ? Math.Max(segMin, 1e-6) : segMin;
                        end = segMax;
                        if (start < end)
                        {
                            intervals.Add((start, end));
                        }
                    }
                }
                else
                {
                    double z = -intercept / slope;
                    if (slope > 0.0)
                    {
                        // Increasing: >0 for expSpot > z
                        start = Math.Max(z, segMin);
                        if (useLogNormal) start = Math.Max(start, 1e-6);
                        end = segMax;
                        if (start < end)
                        {
                            intervals.Add((start, end));
                        }
                    }
                    else
                    {
                        // Decreasing: >0 for expSpot < z
                        start = useLogNormal ? Math.Max(segMin, 1e-6) : segMin; // just ensuring positive minSeg for log Noemal
                        end = Math.Min(z, segMax);
                        if (start < end)
                        {
                            intervals.Add((start, end));
                        }
                    }
                }
            }

            // Add left extrapolation if positive
            if (orderedSegments.Count > 0)
            {
                var leftSeg = orderedSegments[0];
                double leftSlope = leftSeg.Slope;
                double leftIntercept = leftSeg.Intercept;
                double leftMin = double.NegativeInfinity;
                double leftMax = leftSeg.MinSpot;
                if (useLogNormal) leftMin = 1e-6;
                if (leftSlope == 0)
                {
                    if (leftIntercept > 0 && leftMin < leftMax)
                    {
                        intervals.Insert(0, (leftMin, leftMax));
                    }
                }
                else
                {
                    double z = -leftIntercept / leftSlope;
                    if (leftSlope > 0)
                    {
                        // Positive right of z, but left tail is left of MinSpot, so if z < MinSpot, part or all
                        if (z < leftMax)
                        {
                            double start = Math.Max(z, leftMin);
                            if (start < leftMax)
                            {
                                intervals.Insert(0, (start, leftMax));
                            }
                        }
                    }
                    else
                    {
                        // Positive left of z
                        if (z > leftMin)
                        {
                            double end = Math.Min(z, leftMax);
                            if (leftMin < end)
                            {
                                intervals.Insert(0, (leftMin, end));
                            }
                        }
                    }
                }
            }

            // Add right extrapolation if positive
            if (orderedSegments.Count > 0)
            {
                var rightSeg = orderedSegments[orderedSegments.Count - 1];
                double rightSlope = rightSeg.Slope;
                double rightIntercept = rightSeg.Intercept;
                double rightMin = rightSeg.MaxSpot;
                double rightMax = double.PositiveInfinity;
                if (rightSlope == 0)
                {
                    if (rightIntercept > 0 && rightMin < rightMax)
                    {
                        intervals.Add((rightMin, rightMax));
                    }
                }
                else
                {
                    double z = -rightIntercept / rightSlope;
                    if (rightSlope > 0)
                    {
                        // Positive right of z
                        if (z < rightMax)
                        {
                            double start = Math.Max(z, rightMin);
                            if (start < rightMax)
                            {
                                intervals.Add((start, rightMax));
                            }
                        }
                    }
                    else
                    {
                        // Positive left of z
                        if (z > rightMin)
                        {
                            double end = Math.Min(z, rightMax);
                            if (rightMin < end)
                            {
                                intervals.Add((rightMin, end));
                            }
                        }
                    }
                }
            }

            return intervals;
        }

        private static List<(double min, double max)> GetPayoffIntervalsAboveThreshold(List<LinearSegment> linearSegments, double thresholdPnL, bool useLogNormal)
        {
            var intervals = new List<(double min, double max)>();
            var ordered = linearSegments.OrderBy(s => s.MinSpot).ToList();

            foreach (var seg in ordered)
            {
                double a = seg.Slope;
                double b = seg.Intercept;
                double x1 = seg.MinSpot;
                double x2 = seg.MaxSpot;

                // solve a*x + b > thresholdPnL  ⇒  a*x > thresholdPnL - b
                double crit = (a != 0) ? (thresholdPnL - b) / a : double.NaN;

                if (Math.Abs(a) < 1e-12) // flat segment
                {
                    if (b > thresholdPnL)
                    {
                        double start = useLogNormal ? Math.Max(x1, 1e-6) : x1;
                        double end = x2;
                        if (start < end) intervals.Add((start, end));
                    }
                }
                else if (a > 0) // increasing → payoff > thresholdPnL when x > crit
                {
                    double start = Math.Max(crit, x1);
                    if (useLogNormal) start = Math.Max(start, 1e-6);
                    double end = x2;
                    if (start < end) intervals.Add((start, end));
                }
                else // a < 0 → decreasing → payoff > thresholdPnL when x < crit
                {
                    double start = useLogNormal ? Math.Max(x1, 1e-6) : x1;
                    double end = Math.Min(crit, x2);
                    if (start < end) intervals.Add((start, end));
                }
            }

            // left tail extrapolation
            if (ordered.Count > 0)
                AddTailInterval(ordered[0], thresholdPnL, useLogNormal, true, intervals);

            // right tail extrapolation
            if (ordered.Count > 0)
                AddTailInterval(ordered[ordered.Count - 1], thresholdPnL, useLogNormal, false, intervals);

            return intervals;
        }

        private static void AddTailInterval(LinearSegment tailSeg, double threshold, bool useLogNormal, bool isLeftTail, List<(double min, double max)> intervals)
        {
            double a = tailSeg.Slope;
            double b = tailSeg.Intercept;
            double bound = isLeftTail ? tailSeg.MinSpot : tailSeg.MaxSpot;
            double crit = (a != 0) ? (threshold - b) / a : double.NaN;

            double min = isLeftTail ? (useLogNormal ? 1e-6 : double.NegativeInfinity) : bound;
            double max = isLeftTail ? bound : double.PositiveInfinity;

            if (Math.Abs(a) < 1e-12) // flat
            {
                if (b > threshold && min < max)
                    intervals.Insert(isLeftTail ? 0 : intervals.Count, (min, max));
            }
            else if (a > 0) // increasing
            {
                if (isLeftTail)
                {
                    if (crit < bound) // positive region reaches into left tail
                    {
                        double start = Math.Max(crit, min);
                        if (start < bound) intervals.Insert(0, (start, bound));
                    }
                }
                else // right tail
                {
                    double start = Math.Max(crit, bound);
                    if (start < max) intervals.Add((start, max));
                }
            }
            else // a < 0 decreasing constant slope in tail
            {
                if (isLeftTail)
                {
                    double end = Math.Min(crit, bound);
                    if (min < end) intervals.Insert(0, (min, end));
                }
                else
                {
                    if (crit > bound)
                    {
                        double end = Math.Min(crit, max);
                        if (bound < end) intervals.Add((bound, end));
                    }
                }
            }
        }

        private static HashSet<double> GetFiniteVertices(List<LinearSegment> linearSegments)
            {
                var vertices = new HashSet<double>();
                foreach (var linearSegment in linearSegments)
                {
                    if (!double.IsInfinity(linearSegment.MinSpot))
                        vertices.Add(linearSegment.MinSpot);
                    if (!double.IsInfinity(linearSegment.MaxSpot))
                        vertices.Add(linearSegment.MaxSpot);
                }
                return vertices;
            }
        #endregion



        #region PUBLIC METHODS TO ACCESS
        public List<LinearSegment> GetLinearSegments(IBarData barData,
                                                     double totalPnLHitherto = 0,
                                                     List<OptionsWithQuantity> openOptionsWithQuantity = null,
                                                     List<OptionsWithQuantity> targetOptionsWithQuantity = null,
                                                     double? indiaVix = null,
                                                     bool allOrNoneBlackScholes = false)
        {
            var linearSegments = new List<LinearSegment>();
            var strikes = new List<double>();
            double netEntryCashflow = 0.0;
            var callPosByStrike = new Dictionary<double, double>();
            var putPosByStrike = new Dictionary<double, double>();

            bool anyNaN = false;

            // Check for any NaN premiums in both lists
            if (openOptionsWithQuantity != null)
            {
                foreach (var optWithQty in openOptionsWithQuantity)
                {
                    double entry = OptionCommandAccessor.GetOptionPremium(optWithQty.Option);
                    if (double.IsNaN(entry))
                    {
                        anyNaN = true;
                        break;
                    }
                }
            }

            if (!anyNaN && targetOptionsWithQuantity != null)
            {
                foreach (var optWithQty in targetOptionsWithQuantity)
                {
                    double entry = OptionCommandAccessor.GetOptionPremium(optWithQty.Option);
                    if (double.IsNaN(entry))
                    {
                        anyNaN = true;
                        break;
                    }
                }
            }

            // Collect strikes from TradedOptionsList if not null
            if (openOptionsWithQuantity != null)
            {
                strikes.AddRange(openOptionsWithQuantity.Select(o => o.Option.StrikePrice).Distinct());
            }

            // Process TradedOptionsList
            if (openOptionsWithQuantity != null)
            {
                foreach (var optWithQty in openOptionsWithQuantity)
                {
                    var opt = optWithQty.Option;
                    double quantity = optWithQty.Quantity;
                    double entry;

                    if (allOrNoneBlackScholes)
                    {
                        entry = anyNaN  ? BlackScholesCalculator.GetOptionBlackScholesPrice(opt, barData.Close, barData.DateTimeDT, indiaVix: indiaVix)
                                        : OptionCommandAccessor.GetOptionPremium(opt);
                    }
                    else
                    {
                        entry = OptionCommandAccessor.GetOptionPremium(opt);
                        if (double.IsNaN(entry))
                            entry = BlackScholesCalculator.GetOptionBlackScholesPrice(opt, barData.Close, barData.DateTimeDT, indiaVix: indiaVix);
                    }


                    netEntryCashflow += quantity * entry;
                    if (optWithQty.Option.OptionType == OptionType.CE)
                        callPosByStrike[optWithQty.Option.StrikePrice] = quantity;
                    else
                        putPosByStrike[optWithQty.Option.StrikePrice] = quantity;
                }
            }

            // Process targetOptionsWithQuantity
            if (targetOptionsWithQuantity != null)
            {
                foreach (var optWithQty in targetOptionsWithQuantity)
                {
                    var opt = optWithQty.Option;
                    double quantity = optWithQty.Quantity;
                    double entry;

                    if (allOrNoneBlackScholes)
                    {
                        entry = anyNaN ? BlackScholesCalculator.GetOptionBlackScholesPrice(opt, barData.Close, barData.DateTimeDT, indiaVix: indiaVix)
                                        : OptionCommandAccessor.GetOptionPremium(opt);
                    }
                    else
                    {
                        entry = OptionCommandAccessor.GetOptionPremium(opt);
                        if (double.IsNaN(entry))
                            entry = BlackScholesCalculator.GetOptionBlackScholesPrice(opt, barData.Close, barData.DateTimeDT, indiaVix: indiaVix);
                    }

                    netEntryCashflow += quantity * entry;
                    double strike = opt.StrikePrice;
                    if (opt.OptionType == OptionType.CE)
                    {
                        if (callPosByStrike.ContainsKey(strike))
                            callPosByStrike[strike] += quantity;
                        else
                            callPosByStrike[strike] = quantity;
                    }
                    else
                    {
                        if (putPosByStrike.ContainsKey(strike))
                            putPosByStrike[strike] += quantity;
                        else
                            putPosByStrike[strike] = quantity;
                    }
                    if (!strikes.Contains(strike))
                    {
                        strikes.Add(strike);
                    }
                }
            }

            // Sort strikes
            strikes.Sort();

            // Handle case with no options
            if (strikes.Count == 0)
            {
                linearSegments.Add(new LinearSegment { MinSpot = double.NegativeInfinity, MaxSpot = double.PositiveInfinity, Slope = 0, Intercept = totalPnLHitherto - netEntryCashflow });
                return linearSegments;
            }

            double baseConstant = totalPnLHitherto - netEntryCashflow;
            double totalPutPos = putPosByStrike.Values.Sum();
            double currentSlope = -1 * totalPutPos;
            double currentConstant = baseConstant;
            foreach (var kv in putPosByStrike)
            {
                currentConstant += kv.Value * kv.Key;
            }

            // Add the first piece -inf to first strike
            double currentMin = double.NegativeInfinity;
            linearSegments.Add(new LinearSegment { MinSpot = currentMin, MaxSpot = strikes[0], Slope = currentSlope, Intercept = currentConstant });

            // Process each strike
            for (int i = 0; i < strikes.Count; i++)
            {
                double K = strikes[i];
                double callPosAtK = callPosByStrike.ContainsKey(K) ? callPosByStrike[K] : 0;
                double putPosAtK = putPosByStrike.ContainsKey(K) ? putPosByStrike[K] : 0;
                double deltaSlope = callPosAtK + putPosAtK;
                double deltaConstant = -(callPosAtK * K) - (putPosAtK * K);
                currentSlope += deltaSlope;
                currentConstant += deltaConstant;
                currentMin = K;
                double currentMax = (i < strikes.Count - 1) ? strikes[i + 1] : double.PositiveInfinity;
                linearSegments.Add(new LinearSegment { MinSpot = currentMin, MaxSpot = currentMax, Slope = currentSlope, Intercept = currentConstant });
            }

            return linearSegments;
        }


        public double GetPayoffAtSpot(double spot, List<LinearSegment> linearSegments)
        {
            // PayOff using Linear Segments
            if (linearSegments.Count == 0)
                return 0;

            // Find the segment containing expSpot
            foreach (var linearSegment in linearSegments)
            {
                if (spot >= linearSegment.MinSpot && spot < linearSegment.MaxSpot)
                {
                    return linearSegment.Slope * spot + linearSegment.Intercept;
                }
            }

            // Fallback: should never occur due to infinite bounds
            Console.WriteLine($"Warning: No segment found for spot {spot}, returning 0.");
            return 0;
        }


        public double GetPayoffAtSpot(double spot,
                                      List<OptionsWithQuantity> optionsWithQuantity, 
                                      IBarData barData, 
                                      double? indiaVix = null, 
                                      bool allOrNoneBlackScholes = false,
                                      double? lookbackTolerance = null)
        {
            // PayOff using Options Positions
            if (optionsWithQuantity == null || optionsWithQuantity.Count == 0)
                return 0.0;

            double pnl = 0.0;

            bool anyNaN = false;                 
            if (optionsWithQuantity != null)
            {
                foreach (var optWithQty in optionsWithQuantity)
                {
                    double premium = OptionCommandAccessor.GetOptionPremium(optWithQty.Option, lookbackTolerance);
                    if (double.IsNaN(premium))
                    {
                        anyNaN = true;
                        break;
                    }
                }
            }


            foreach (var optWithQty in optionsWithQuantity)
            {
                var opt = optWithQty.Option;
                var quantity = optWithQty.Quantity;
               
                double entry;
                if (allOrNoneBlackScholes)
                {
                    entry = anyNaN ? BlackScholesCalculator.GetOptionBlackScholesPrice(opt, barData.Close, barData.DateTimeDT, indiaVix: indiaVix)
                                    : OptionCommandAccessor.GetOptionPremium(opt, lookbackTolerance);
                }
                else
                {
                    entry = OptionCommandAccessor.GetOptionPremium(opt, lookbackTolerance);
                    if (double.IsNaN(entry))
                        entry = BlackScholesCalculator.GetOptionBlackScholesPrice(opt, barData.Close, barData.DateTimeDT, indiaVix: indiaVix);
                }

                double intrinsic = (opt.OptionType == OptionType.CE) ? Math.Max(spot - opt.StrikePrice, 0) : Math.Max(opt.StrikePrice - spot, 0);
                pnl += quantity * (intrinsic - entry);
            }
            return pnl;
        }



        public double GetPayoffAtSpotAtDateX(double expSpot,                  
                                               List<OptionsWithQuantity> optionsWithQuantity,
                                               IBarData barData,                         
                                               DateTime valuationDateTime,               
                                               double? indiaVix = null,
                                               bool allOrNoneBlackScholes = false,
                                               double? lookbackTolerance = null )
        {
            if (optionsWithQuantity == null || optionsWithQuantity.Count == 0)
                return 0.0;

            double pnl = 0.0;
            bool anyNaN = false;

            // Check if any entry premium is missing
            if (optionsWithQuantity != null)
            {
                foreach (var optWithQty in optionsWithQuantity)
                {
                    double premium = OptionCommandAccessor.GetOptionPremium(optWithQty.Option, lookbackTolerance);
                    if (double.IsNaN(premium))
                    {
                        anyNaN = true;
                        break;
                    }
                }
            }

            foreach (var optWithQty in optionsWithQuantity)
            {
                var opt = optWithQty.Option;
                var quantity = optWithQty.Quantity;

                double entry;
                if (allOrNoneBlackScholes)
                {
                    entry = anyNaN
                        ? BlackScholesCalculator.GetOptionBlackScholesPrice(opt, barData.Close, barData.DateTimeDT, indiaVix: indiaVix)
                        : OptionCommandAccessor.GetOptionPremium(opt, lookbackTolerance);
                }
                else
                {
                    entry = OptionCommandAccessor.GetOptionPremium(opt, lookbackTolerance);
                    if (double.IsNaN(entry))
                        entry = BlackScholesCalculator.GetOptionBlackScholesPrice(opt, barData.Close, barData.DateTimeDT, indiaVix: indiaVix);
                }


                double futureValue = BlackScholesCalculator.GetOptionBlackScholesPriceAtDateX(opt, barData.Close, valuationDateTime, indiaVix);


                // Use current theoretical value instead of intrinsic
                pnl += quantity * (futureValue - entry);
            }

            return pnl;
        }



        public PayOffMetrics GetPayOffMetrics(List<LinearSegment> linearSegments,
                                      IBarData barData,
                                      double underLyingPrice,
                                      DateTime expiryDT,
                                      bool useLogNormal = false,
                                      double? indiaVix = null)
        {
            double maxProfit = double.NaN;
            double maxLoss = double.NaN;
            double riskReward = double.NaN;
            double POP = double.NaN;
            double POL = double.NaN;
            double PMP = double.NaN;
            double PML = double.NaN;
            double EV = double.NaN;
            double EdgeFactor = double.NaN;
            double RightMostPayOff = double.NaN;
            double LeftMostPayoff = double.NaN;

            PayOffMetrics payOffBenchmark = new PayOffMetrics
            {
                MaxProfit = maxProfit,
                MaxLoss = maxLoss,
                RiskReward = riskReward,
                POP = POP,
                POL = POL,
                PMP = PMP,
                PML = PML,
                ExpectedValue = EV,
                EdgeFactor = EdgeFactor,
                LeftMostPayoff = LeftMostPayoff,
                RightMostPayoff = RightMostPayOff
            };

            // Sort segments by MinSpot to ensure correct slope and vertex processing
            var orderedSegments = linearSegments.OrderBy(s => s.MinSpot).ToList();
            var DaysDiff = expiryDT - barData.DateTimeDT;
            double TTEinDays = DaysDiff.TotalDays;

            Debug.Assert(TTEinDays > 0);

            double LTP = Math.Max(underLyingPrice, 1e-6);

            // ── Extreme asymptotic payoffs ──
            var leftSeg = orderedSegments[0];
            var rightSeg = orderedSegments[orderedSegments.Count - 1];

            payOffBenchmark.LeftMostPayoff = Math.Abs(leftSeg.Slope) < 1e-9
                ? leftSeg.Intercept
                : leftSeg.Slope > 0 ? double.NegativeInfinity : double.PositiveInfinity;

            payOffBenchmark.RightMostPayoff = Math.Abs(rightSeg.Slope) < 1e-9
                ? rightSeg.Intercept
                : rightSeg.Slope > 0 ? double.PositiveInfinity : double.NegativeInfinity;

            // Compute MaxProfit, MaxLoss, RiskReward
            double leftSlope = orderedSegments[0].Slope;
            double rightSlope = orderedSegments[orderedSegments.Count - 1].Slope;

            bool lossUnbounded = leftSlope > 0 || rightSlope < 0;
            bool profitUnbounded = leftSlope < 0 || rightSlope > 0;

            // Infinite Loss / profit
            maxLoss = lossUnbounded ? double.NegativeInfinity : double.NaN;
            maxProfit = profitUnbounded ? double.PositiveInfinity : double.NaN;

            var vertices = GetFiniteVertices(orderedSegments).ToList();
            vertices = vertices.OrderBy(v => v).Distinct().ToList();
            Debug.Assert(vertices.Count != 0);

            var payoffs = vertices.Select(v => GetPayoffAtSpot(v, orderedSegments)).ToList();

            // Finite Profit / Loss
            if (!lossUnbounded) maxLoss = payoffs.Min();
            if (!profitUnbounded) maxProfit = payoffs.Max();

            if (maxLoss >= 0 || maxProfit <= 0)
                return payOffBenchmark;

            riskReward = double.IsNegativeInfinity(maxLoss) ? 0 : double.IsPositiveInfinity(maxProfit) ? double.PositiveInfinity : Math.Abs(maxProfit / maxLoss);

            payOffBenchmark.MaxLoss = maxLoss;
            payOffBenchmark.MaxProfit = maxProfit;
            payOffBenchmark.RiskReward = riskReward;

            double ATM_IV = indiaVix.HasValue ? BlackScholesCalculator.GetAtmIVfromIndiaVix(indiaVix.Value, TTEinDays)
                                              : BlackScholesCalculator.GetAtmIV(underLyingPrice, barData.DateTimeDT, expiryDT);

            double dailyVolatility = ATM_IV / Math.Sqrt(365);

            if (!useLogNormal)
            {
                double STDdev = dailyVolatility * Math.Sqrt(TTEinDays);
                double AbsStdev = LTP * STDdev;

                Debug.Assert(AbsStdev > 0);

                if (!double.IsPositiveInfinity(maxProfit))
                {
                    var maxProfitIntervals = GetFlatIntervalsForValue(orderedSegments, maxProfit, false);
                    PMP = ComputeNormalProbOverIntervals(maxProfitIntervals, LTP, AbsStdev);
                }

                if (!double.IsNegativeInfinity(maxLoss))
                {
                    var maxLossIntervals = GetFlatIntervalsForValue(orderedSegments, maxLoss, false);
                    PML = ComputeNormalProbOverIntervals(maxLossIntervals, LTP, AbsStdev);
                }

                var positiveIntervals = GetPositivePayoffIntervals(orderedSegments, false);
                POP = ComputeNormalProbOverIntervals(positiveIntervals, LTP, AbsStdev);
                POL = 100 - POP;

                payOffBenchmark.POL = POL;
                payOffBenchmark.POP = POP;
                payOffBenchmark.PML = PML;
                payOffBenchmark.PMP = PMP;

                // [FIX #2] Removed early return for unbounded slopes.
                // Integration is clamped to [LTP-6σ, LTP+6σ], so unbounded payoff
                // outside this range is irrelevant (probability mass < 10^-9).

                double lowerBound = Math.Max(LTP - 6 * AbsStdev, 0);
                double upperBound = LTP + 6 * AbsStdev;
                Func<double, double> integrand = spot =>
                {
                    double payoff = GetPayoffAtSpot(spot, orderedSegments);
                    double pdf = Normal.PDF(LTP, AbsStdev, spot);
                    return payoff * pdf;
                };

                // [FIX #4] Split integration at every vertex (kink point).
                // Each sub-interval has a smooth integrand (linear payoff × Gaussian),
                // so GK achieves full accuracy regardless of segment count.
                var breakpoints = new List<double> { lowerBound };
                breakpoints.AddRange(vertices.Where(v => v > lowerBound && v < upperBound));
                breakpoints.Add(upperBound);

                EV = 0;
                bool integrationFailed = false;
                for (int i = 0; i < breakpoints.Count - 1; i++)
                {
                    double segEV = GaussKronrodRule.Integrate(integrand, breakpoints[i], breakpoints[i + 1],
                        out double errorEstimate, out double subintervalsUsed, targetRelativeError: 1e-8);

                    if (double.IsNaN(segEV))
                    {
                        integrationFailed = true;
                        break;
                    }
                    EV += segEV;
                }

                if (integrationFailed)
                {
                    // [FIX #3] Reset EV to 0 before fallback accumulation.
                    EV = 0;
                    foreach (var segment in orderedSegments)
                    {
                        double minSpot = segment.MinSpot;
                        double maxSpot = segment.MaxSpot;
                        double prob;
                        double payoff;
                        if (minSpot == double.NegativeInfinity)
                        {
                            payoff = segment.Intercept; // Constant payoff
                            prob = Normal.CDF(LTP, AbsStdev, maxSpot);
                        }
                        else if (maxSpot == double.PositiveInfinity)
                        {
                            payoff = segment.Intercept; // Constant payoff
                            prob = 1.0 - Normal.CDF(LTP, AbsStdev, minSpot);
                        }
                        else
                        {
                            prob = Normal.CDF(LTP, AbsStdev, maxSpot) - Normal.CDF(LTP, AbsStdev, minSpot);
                            if (prob <= 0) continue;
                            double midSpot = (minSpot + maxSpot) / 2;
                            payoff = GetPayoffAtSpot(midSpot, orderedSegments);
                        }
                        if (prob <= 0) continue;
                        EV += prob * payoff;
                    }
                }

                payOffBenchmark.ExpectedValue = EV;
            }
            else
            {
                double sigma = dailyVolatility * Math.Sqrt(TTEinDays);
                Debug.Assert(sigma > 0 && LTP > 0);

                double mu_log = Math.Log(LTP) - (sigma * sigma / 2);

                if (!double.IsPositiveInfinity(maxProfit))
                {
                    var maxProfitIntervals = GetFlatIntervalsForValue(orderedSegments, maxProfit, true);
                    PMP = ComputeLogNormalProbOverIntervals(maxProfitIntervals, mu_log, sigma);
                }

                if (!double.IsNegativeInfinity(maxLoss))
                {
                    var maxLossIntervals = GetFlatIntervalsForValue(orderedSegments, maxLoss, true);
                    PML = ComputeLogNormalProbOverIntervals(maxLossIntervals, mu_log, sigma);
                }

                var positiveIntervals = GetPositivePayoffIntervals(orderedSegments, true);
                POP = ComputeLogNormalProbOverIntervals(positiveIntervals, mu_log, sigma);
                POL = 100 - POP;

                payOffBenchmark.POL = POL;
                payOffBenchmark.POP = POP;
                payOffBenchmark.PML = PML;
                payOffBenchmark.PMP = PMP;

                // [FIX #2] Removed early return for unbounded slopes.
                // Integration is clamped to [1e-6, exp(mu_log + 6σ)], so unbounded payoff
                // outside this range is irrelevant.

                double lowerBound = 1e-6;
                double upperBound = Math.Exp(mu_log + 6 * sigma);
                Func<double, double> integrand = spot =>
                {
                    if (spot <= 0) return 0.0;
                    double payoff = GetPayoffAtSpot(spot, orderedSegments);
                    double pdf = LogNormal.PDF(mu_log, sigma, spot);
                    return payoff * pdf;
                };

                // [FIX #4] Split integration at every vertex (kink point).
                var breakpoints = new List<double> { lowerBound };
                breakpoints.AddRange(vertices.Where(v => v > lowerBound && v < upperBound));
                breakpoints.Add(upperBound);

                EV = 0;
                bool integrationFailed = false;
                for (int i = 0; i < breakpoints.Count - 1; i++)
                {
                    double segEV = GaussKronrodRule.Integrate(integrand, breakpoints[i], breakpoints[i + 1],
                        out double errorEstimate, out double subintervalsUsed, targetRelativeError: 1e-8);

                    if (double.IsNaN(segEV))
                    {
                        integrationFailed = true;
                        break;
                    }
                    EV += segEV;
                }

                if (integrationFailed)
                {
                    // [FIX #3] Reset EV to 0 before fallback accumulation.
                    EV = 0;
                    foreach (var segment in orderedSegments)
                    {
                        double minSpot = segment.MinSpot;
                        double maxSpot = segment.MaxSpot;
                        double prob;
                        double payoff;
                        if (minSpot == double.NegativeInfinity)
                        {
                            payoff = segment.Intercept; // Constant payoff
                            prob = LogNormal.CDF(maxSpot, mu_log, sigma);
                        }
                        else if (maxSpot == double.PositiveInfinity)
                        {
                            payoff = segment.Intercept; // Constant payoff
                            prob = 1.0 - LogNormal.CDF(minSpot, mu_log, sigma);
                        }
                        else
                        {
                            prob = LogNormal.CDF(maxSpot, mu_log, sigma) - LogNormal.CDF(minSpot, mu_log, sigma);
                            if (prob <= 0) continue;
                            double midSpot = (minSpot + maxSpot) / 2;
                            payoff = GetPayoffAtSpot(midSpot, orderedSegments);
                        }
                        if (prob <= 0) continue;
                        EV += prob * payoff;
                    }
                }

                payOffBenchmark.ExpectedValue = EV;
            }

            double edgeFactor = (double.IsNegativeInfinity(maxLoss)) ? double.NaN : EV / -maxLoss;
            // [FIX #1] Assign edgeFactor to the output struct.
            payOffBenchmark.EdgeFactor = edgeFactor;

            return payOffBenchmark;
        }


        /// <summary>
        /// Computes the probability that the underlying spot price, evaluated X days
        /// from today (or at option expiry if X days is null), falls within regions
        /// where the strategy’s expiry PnL exceeds a given threshold. The probability
        /// is derived from the payoff curve and the assumed spot price distribution.
        /// </summary>
        /// <param name="linearSegments">
        /// Piecewise-linear representation of the strategy’s expiry payoff (PnL vs spot).
        /// </param>       
        /// <param name="optExpiryDT">
        /// Expiry date and time of the option strategy.
        /// </param>
        /// <param name="thresholdPnL">
        /// Expiry PnL level for which the probability is to be computed.
        /// </param>
        /// <param name="Xdays">
        /// Time horizon in days at which the probability is evaluated; if null, the
        /// evaluation is performed at option expiry.
        /// </param>
        /// <returns>
        /// Probability that the underlying spot price lies in regions where the
        /// expiry PnL exceeds the specified threshold.
        /// </returns>


        public double ProbAboveGivenExpiryPnL(List<LinearSegment> linearSegments,
                                                          IBarData barData,
                                                          double underLyingPrice,
                                                          DateTime expiryDT,                                              
                                                          double thresholdPnL,
                                                          double? Xdays = null,                                             
                                                          double? indiaVix = null,
                                                          bool useLogNormal = false)

        {
          

            var orderedSegments = linearSegments.OrderBy(s => s.MinSpot).ToList();

            double LTP = Math.Max(underLyingPrice, 1e-6);

            double TTEinDays = (expiryDT - barData.DateTimeDT).TotalDays;

            double ATM_IV = indiaVix.HasValue ? BlackScholesCalculator.GetAtmIVfromIndiaVix(indiaVix.Value, TTEinDays) 
                                              : BlackScholesCalculator.GetAtmIV(underLyingPrice, barData.DateTimeDT, expiryDT);
            double dailyVolatility = ATM_IV / Math.Sqrt(365);

            double xdays = Xdays ?? TTEinDays;

            if (!useLogNormal)
            {
                double STDdev = dailyVolatility * Math.Sqrt(xdays);
                double AbsStdev = LTP * STDdev;
                Debug.Assert(AbsStdev > 0);

                var payOffIntervalsAboveThreshold = GetPayoffIntervalsAboveThreshold(orderedSegments, thresholdPnL, useLogNormal: false);
                return ComputeNormalProbOverIntervals(payOffIntervalsAboveThreshold, LTP, AbsStdev);               
            }
            else
            {
                double sigma = dailyVolatility * Math.Sqrt(xdays);
                Debug.Assert(sigma > 0 && LTP > 0);

                double mu_log = Math.Log(LTP) - (sigma * sigma / 2);

                var payOffIntervalsAboveThreshold = GetPayoffIntervalsAboveThreshold(orderedSegments, thresholdPnL, useLogNormal: true);
                return ComputeLogNormalProbOverIntervals(payOffIntervalsAboveThreshold, mu_log, sigma);

            }

        }




        public double ProbBelowGivenExpiryPnL(List<LinearSegment> linearSegments,
                                                          IBarData barData,
                                                          double underLyingPrice,
                                                          DateTime expiryDT,
                                                          double thresholdPnL,
                                                          double? Xdays = null,
                                                          double? indiaVix = null,
                                                          bool useLogNormal = false)
        {
            return 100 - ProbAboveGivenExpiryPnL(linearSegments, barData, underLyingPrice, expiryDT, thresholdPnL, Xdays, indiaVix, useLogNormal);
        }



        /// <summary>
        /// Computes the probability that the spot, evaluated X days
        /// from today (or at option expiry if X days is null), will be above
        /// a given threshold. The probabilityis derived from the payoff curve 
        /// and the assumed spot price distribution.
        /// </summary>
        /// <param name="linearSegments">
        /// Piecewise-linear representation of the strategy’s expiry payoff (PnL vs spot).
        /// </param>       
        /// <param name="optExpiryDT">
        /// Expiry date and time of the option strategy.
        /// </param>
        /// <param name="thresholdSpot">
        /// Spot level for which the probability is to be computed.
        /// </param>
        /// <param name="Xdays">
        /// Time horizon in days at which the probability is evaluated; if null, the
        /// evaluation is performed at option expiry.
        /// </param>
        /// <returns>
        /// Probability that the underlying spot price lies in regions where the
        /// expiry PnL exceeds the specified threshold.
        /// </returns>


        public double ProbAboveGivenSpot(IBarData barData, double underLyingPrice,
                                            DateTime expiryDT,
                                            double thresholdSpot,
                                            double? Xdays = null,
                                            double? indiaVix = null,
                                            bool useLogNormal = false)

        {


           
            double LTP = Math.Max(underLyingPrice, 1e-6);

            double TTEinDays = (expiryDT - barData.DateTimeDT).TotalDays;

            double ATM_IV = indiaVix.HasValue ? BlackScholesCalculator.GetAtmIVfromIndiaVix(indiaVix.Value, TTEinDays)
                                              : BlackScholesCalculator.GetAtmIV(underLyingPrice, barData.DateTimeDT, expiryDT);

            double dailyVolatility = ATM_IV / Math.Sqrt(365);

            double xdays = Xdays ?? TTEinDays;

            if (!useLogNormal)
            {
                double STDdev = dailyVolatility * Math.Sqrt(xdays);
                double AbsStdev = LTP * STDdev;
                Debug.Assert(AbsStdev > 0);

                var payOffIntervalsAboveSpot = new List<(double min, double max)>  { (thresholdSpot, double.PositiveInfinity)  };
                return ComputeNormalProbOverIntervals(payOffIntervalsAboveSpot, LTP, AbsStdev);
            }
            else
            {
                double sigma = dailyVolatility * Math.Sqrt(xdays);
                Debug.Assert(sigma > 0 && LTP > 0);

                double mu_log = Math.Log(LTP) - (sigma * sigma / 2);

                var payOffIntervalsAboveSpot = new List<(double min, double max)> { (thresholdSpot, double.PositiveInfinity) };
                return ComputeLogNormalProbOverIntervals(payOffIntervalsAboveSpot, mu_log, sigma);

            }

        }


        public double ProbBelowGivenSpot(IBarData barData, double underLyingPrice,
                                            DateTime expiryDT,
                                            double thresholdSpot,
                                            double? Xdays = null,
                                            double? indiaVix = null,
                                            bool useLogNormal = false)
        {
            return 100 - ProbAboveGivenSpot(barData, underLyingPrice, expiryDT, thresholdSpot, Xdays, indiaVix, useLogNormal);
        }

        public double GetNetCredit(List<OptionsWithQuantity> optionsWithQuantity, IBarData barData, double? indiaVix = null, bool allOrNoneBlackScholes = false)
        {
            if (optionsWithQuantity == null || optionsWithQuantity.Count == 0)
                return 0.0;

            double netCredit = 0.0;

            bool anyNaN = false;
            if (optionsWithQuantity != null)
            {
                foreach (var optWithQty in optionsWithQuantity)
                {
                    double premium = OptionCommandAccessor.GetOptionPremium(optWithQty.Option);
                    if (double.IsNaN(premium))
                    {
                        anyNaN = true;
                        break;
                    }
                }
            }


            foreach (var optWithQty in optionsWithQuantity)
            {
                var opt = optWithQty.Option;
                double quantity = optWithQty.Quantity;

                double premium;
                if (allOrNoneBlackScholes)
                {
                    premium = anyNaN ? BlackScholesCalculator.GetOptionBlackScholesPrice(opt, barData.Close, barData.DateTimeDT, indiaVix: indiaVix)
                                    : OptionCommandAccessor.GetOptionPremium(opt);
                }
                else
                {
                    premium = OptionCommandAccessor.GetOptionPremium(opt);
                    if (double.IsNaN(premium))
                        premium = BlackScholesCalculator.GetOptionBlackScholesPrice(opt, barData.Close, barData.DateTimeDT, indiaVix: indiaVix);
                }

                netCredit += quantity * premium * (-1);  // flip sign: sell → +credit
            }

            return netCredit;

        }

        

        public List<(double BreakPoint, BreakEvenType Type)> GetBreakEvenPointsWithTypes(List<LinearSegment> linearSegments)
        {
            var breakEvens = new List<(double, BreakEvenType)>();
            const double tolerance = 1e-6; // Tolerance for floating-point comparison
            const double slopeThreshold = 1e-6; // Minimum slope to avoid numerical instability

            foreach (var segment in linearSegments)
            {
                if (Math.Abs(segment.Slope) > slopeThreshold) // Check for non-near-zero slope
                {
                    double zeroSpot = -segment.Intercept / segment.Slope;
                    if (!double.IsInfinity(zeroSpot) && !double.IsNaN(zeroSpot) &&
                        zeroSpot >= segment.MinSpot - tolerance && zeroSpot < segment.MaxSpot + tolerance)
                    {
                        BreakEvenType type = segment.Slope > 0 ? BreakEvenType.BreakUp : BreakEvenType.BreakDown;
                        breakEvens.Add((zeroSpot, type));
                    }
                }
            }

            breakEvens.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            return breakEvens;
        }


        public List<KeyValuePair<double, double>> CreatePayOffChartTable(double baseSpot, List<LinearSegment> linearSegments, double gap = 10, double range = 50000)
        {
            List<KeyValuePair<double, double>> payoffChart = new List<KeyValuePair<double, double>>();

            double minSpot = baseSpot - range;
            double maxSpot = baseSpot + range;

            // Ensure gap is positive to avoid infinite loops
            if (gap <= 0)
                throw new ArgumentException("Gap must be positive.", nameof(gap));

            // Adjust gap to limit number of points (e.g., target ~100-200 points)
            double estimatedPoints = (maxSpot - minSpot) / gap;
            if (estimatedPoints > 200)
                gap = (maxSpot - minSpot) / 200;

            // Iterate from minSpot to maxSpot with specified gap
            double spotPrice = minSpot;
            while (spotPrice <= maxSpot + 1e-6) // Small epsilon for floating-point precision
            {
                double payOff = GetPayoffAtSpot(spotPrice, linearSegments);
                payoffChart.Add(new KeyValuePair<double, double>(spotPrice, payOff));
                spotPrice += gap;
            }

            return payoffChart;
        }


        



        #endregion









    }

}
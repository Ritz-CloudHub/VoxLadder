using QX.BackTesting.Indicators;
using QX.BackTesting.Strategies;
using QX.FinLib.Common;            // <-- OptionType lives here
using QX.FinLib.Data;
using QX.FinLib.Instrument;
using QX.FinLib.TSOptions;
using QX.OptionsBTTestApp.RD;
using System;
using System.Collections.Generic;
using System.Data.Entity.Core.Common.CommandTrees.ExpressionBuilder;
using System.Diagnostics;          // <-- Debug
using System.Linq;
using static QX.BackTesting.Strategies.VoxLadder;

namespace QX.BackTesting.Strategies
{
    public partial class VoxLadder
    {
        public enum SortBy
        {
            MaxProfit,
            MaxLoss,
            ProbOfProfit,
            ProbOfLoss,
            ProbOfMaxProfit,
            ProbOfMaxLoss,
            RiskReward,
            ExpValue,
            EdgeFactor,
            RightMostPayoff,
            LeftMostPayoff
        }

        public readonly struct OptionToTrade
        {
            public OptionsInstrument Option { get; }
            public OrderSide OrderSide { get; }
            public int LegOffset { get; }
            public int BaseLots { get; }
            public double? LimitPriceToEnter { get; }
            public bool IsSpreadLeg { get; }

            public OptionToTrade(OptionsInstrument option, OrderSide orderSide, int legOffset, int baseLots, bool isSpreadLeg, double? limitPriceEnter = null)
            {
                Option = option;
                OrderSide = orderSide;
                LegOffset = legOffset;
                BaseLots = baseLots;
                LimitPriceToEnter = limitPriceEnter;
                IsSpreadLeg = isSpreadLeg;
            }
        }

        public class TradeSetup
        {
            /// <summary>
            /// MaxProfit, MaxLoss, ExpValue have been generated for 1 Lot
            /// </summary>
            public TradeType TradeType { get; set; }
            public double MaxProfit { get; set; }
            public double MaxLoss { get; set; }
            public double SpreadCredit { get; set; }   
            public double ProbOfProfitSpread { get; set; }
            public double ProbOfProfit { get; set; }
            public double ProbOfLoss { get; set; }
            public double ProbOfMaxProfit { get; set; }
            public double ProbOfMaxLoss { get; set; }
            public double RiskReward { get; set; }
            public double ExpValue { get; set; }
            public double EdgeFactor { get; set; }
            public double RightMostPayoff { get; set; }
            public double LeftMostPayoff { get; set; }
            public int LegCount { get; set; }
            public List<int?> InitialOffsets { get; set; }
            public List<OptionToTrade> OptionsToTradeList { get; set; } = new List<OptionToTrade>();
            public List<LinearSegment> LinearSegments { get; set; }

        }


        // Moved to StrategyParameters.cs: SpreadLotsPerTrade



      
       
        public TradeSetup GetBenchMarkValuesPerLot(IBarData barData,
                                                        double synFuture,
                                                        List<OptionsWithQuantity> targetOptionsWithQuantity, 
                                                        bool allOrNoneBlackScholes = false)
        {
            var tradeSetup = new TradeSetup();
            PayOffMetrics payOffMetrics;
            var linearSegments = PayOffChartHelper.GetLinearSegments(barData,
                                                                     totalPnLHitherto: 0,
                                                                     targetOptionsWithQuantity: targetOptionsWithQuantity,
                                                                     indiaVix: CurrentBarVIX,
                                                                     allOrNoneBlackScholes: allOrNoneBlackScholes);


            var expiryDateTime = targetOptionsWithQuantity.FirstOrDefault().Option.ContractExpiration.Date + DayEndTime;

            payOffMetrics = PayOffChartHelper.GetPayOffMetrics(linearSegments, barData, synFuture, expiryDateTime, indiaVix: CurrentBarVIX);

            tradeSetup.ProbOfProfit = payOffMetrics.POP;
            tradeSetup.ProbOfMaxProfit = payOffMetrics.PMP;
            tradeSetup.ProbOfLoss = payOffMetrics.POL;
            tradeSetup.ProbOfMaxLoss = payOffMetrics.PML;
            tradeSetup.ExpValue = payOffMetrics.ExpectedValue;
            tradeSetup.MaxProfit = payOffMetrics.MaxProfit;
            tradeSetup.MaxLoss = payOffMetrics.MaxLoss;
            tradeSetup.RiskReward = payOffMetrics.RiskReward;
            tradeSetup.LinearSegments = linearSegments;
            tradeSetup.LeftMostPayoff = payOffMetrics.LeftMostPayoff;
            tradeSetup.RightMostPayoff = payOffMetrics.RightMostPayoff;
            tradeSetup.ExpValue = (tradeSetup.MaxLoss >= 0 || tradeSetup.MaxProfit <= 0) ? double.NegativeInfinity : tradeSetup.ExpValue;
            tradeSetup.EdgeFactor = (tradeSetup.MaxLoss >= 0 || tradeSetup.MaxProfit <= 0) ? double.NegativeInfinity : tradeSetup.ExpValue / Math.Abs(tradeSetup.MaxLoss);


            var spreadList = targetOptionsWithQuantity.Take(2).ToList(); // Only First Two Option Credit Spread
            var spreadCredit = PayOffChartHelper.GetNetCredit(spreadList, barData, indiaVix: CurrentBarVIX, allOrNoneBlackScholes: false);

            tradeSetup.SpreadCredit = spreadCredit;


            var sellOpt = spreadList[0].Option;
            var spreadCreditPerLot = spreadCredit / LotSize;
            int dir = sellOpt.OptionType == OptionType.PE ? 1 : -1;
            double breakEvenPoint = sellOpt.StrikePrice - dir * spreadCreditPerLot;
            double probProfitSpread = dir > 0 ? PayOffChartHelper.ProbAboveGivenSpot(barData, synFuture, LadderExpiryDT, breakEvenPoint, indiaVix: CurrentBarVIX)
                                             : PayOffChartHelper.ProbBelowGivenSpot(barData, synFuture, LadderExpiryDT, breakEvenPoint, indiaVix: CurrentBarVIX);


            tradeSetup.ProbOfProfitSpread = probProfitSpread;

            return tradeSetup;

        }


       

        public bool CreateSpreadLegs(TradeType tradeType, IBarData barData, double synFuture,  
                                     out TradeSetup benchMarkToTrade, out TradeParameters tradeParameters,
                                     double? targetMaxLossPct = null, double? targetPOP = null)
        {

           
            benchMarkToTrade = new TradeSetup();
            tradeParameters = new TradeParameters();


            //const double A = 25;  // hyperbolic capping for Vix
            //double daysToExpiry = (LadderExpiryDT - barData.DateTimeDT).TotalDays;
            //double tanhVix = A * Math.Tanh(CurrentBarVIX / A);
            //double annualStdDevPct = tanhVix;                                 // Vix as annualised Vol/SD in pct
            //double dailyStdDevPct = annualStdDevPct / Math.Sqrt(252);         // 1 SD Pct move in one day
            //double dteStdDevPct = dailyStdDevPct * Math.Sqrt(daysToExpiry);   // 1 SD Pct move till expiry

            int dir = tradeType == TradeType.BullLadder ? 1 : -1;

            OptionType optType = dir > 0 ? OptionType.PE : OptionType.CE;
            // OptionType farOptType = dir > 0 ? OptionType.CE : OptionType.PE;  // Moved to CreateLadderTradeLeg


            if (dbg)
            {
                int x = 0;
            }


            #region SELL LEG SELECTION

                   
            double targetSellPremium = TargetSellPremium;
            var sellOpt = OptionCommandAccessor.GetOptionByPremium(optType, LadderExpiryDT, targetSellPremium, out double sellOptPremium, out _, lookbackTolerance: 10);
            double sellStrike =double.NaN;

            if (sellOpt != null && sellOpt.StrikePrice % 100 != 0)
            {
                // Rounding to 100
                sellStrike = StrikeRoundUp(sellOpt.StrikePrice, useFloor: dir > 0);
                sellOpt = OptionCommandAccessor.GetOption(optType, sellStrike, LadderExpiryDT);
                if (sellOpt != null)
                    sellOptPremium = OptionCommandAccessor.GetOptionPremium(sellOpt, lookbackTolerance: 10);
            }

            if (sellOpt == null || double.IsNaN(sellOptPremium) ||  sellOptPremium > 2.0 * targetSellPremium)
            {
                // Black Scholes for back test   
               
                double TTEinDays = (LadderExpiryDT - barData.DateTimeDT).TotalDays;
                double atm_IV = BlackScholesCalculator.GetAtmIVfromIndiaVix(CurrentBarVIX, TTEinDays);
                sellStrike = StrikeRoundUp(BlackScholesCalculator.GetStrikeFromOptionPrice(optType, targetSellPremium, synFuture, atm_IV, TTEinDays), useFloor: dir > 0);
                sellOpt = OptionCommandAccessor.GetOption(optType, sellStrike, LadderExpiryDT);

                if (sellOpt == null)
                {                   
                    // Add Option in Options Instrument List                           
                    string sellOptName = OptionCommandAccessor.GetOptionName(optType, sellStrike, LadderExpiryDT);
                    if (!OptionCommandAccessor.AddOption(sellOptName, out sellOpt))
                        return false;
                }
                _sl?.WriteLine($"{barData.DateTimeDT}: Sell Strike selected using BlackScholes  {sellOpt.InstrumentName}");

            }

           

            double sellWidth = Math.Abs(sellOpt.StrikePrice - synFuture);


            Debug.Assert(sellOpt != null);
            #endregion


            #region BUY LEG SELECTION

         
            sellOptPremium = OptionCommandAccessor.GetOptionPremium(sellOpt, lookbackTolerance: 10);
            if (double.IsNaN(sellOptPremium))
            {
                //double TTEinDays = (LadderExpiryDT - barData.DateTimeDT).TotalDays;
                sellOptPremium = BlackScholesCalculator.GetOptionBlackScholesPrice(sellOpt, synFuture, barData.DateTimeDT, CurrentBarVIX);
            }
            double targetBuyPremium = sellOptPremium * BuyPremiumRatio;
            var buyOpt = OptionCommandAccessor.GetOptionByPremium(optType, LadderExpiryDT, targetBuyPremium, out double buyOptPremium, out _, lookbackTolerance: 10);
            var buyStrike =double.NaN;

            if (buyOpt != null && buyOpt.StrikePrice % 100 != 0)
            {
                buyStrike = StrikeRoundUp(buyOpt.StrikePrice, useFloor: dir > 0);
                buyOpt = OptionCommandAccessor.GetOption(optType, buyStrike, LadderExpiryDT);
                if (buyOpt != null)
                    buyOptPremium = OptionCommandAccessor.GetOptionPremium(buyOpt, lookbackTolerance: 10);
            }

            if (buyOpt == null || double.IsNaN(buyOptPremium) || buyOptPremium > 2 * targetBuyPremium)
            {
                // If option still null or premium more than upper bound
                // Assuming Data missing
                // Fetch option using Black Scholes
                // Black Scholes for back test   
              
                double TTEinDays = (LadderExpiryDT - barData.DateTimeDT).TotalDays;
                double atm_IV = BlackScholesCalculator.GetAtmIVfromIndiaVix(CurrentBarVIX, TTEinDays);
                buyStrike = StrikeRoundUp(BlackScholesCalculator.GetStrikeFromOptionPrice(optType, targetBuyPremium, synFuture, atm_IV, TTEinDays), useFloor: dir > 0);
                buyOpt = OptionCommandAccessor.GetOption(optType, buyStrike, LadderExpiryDT);
                if (buyOpt == null)
                {
                    // Add Option in Options Instrument List                   
                    string buyOptName = OptionCommandAccessor.GetOptionName(optType, buyStrike, LadderExpiryDT);
                    if (!OptionCommandAccessor.AddOption(buyOptName, out buyOpt))
                        return false;
                   
                }
                _sl?.WriteLine($"{barData.DateTimeDT}: Buy Strike Selected using BlackScholes {buyOpt.InstrumentName}");
            }

           


            double buyWidth = Math.Abs(buyOpt.StrikePrice - synFuture);


            // Ensure minimum spread of 100 points
            double spreadWidth = dir * (sellOpt.StrikePrice - buyOpt.StrikePrice);
            if (spreadWidth < MinSpreadWidth)
            {

                spreadWidth = MinSpreadWidth;
                buyWidth = sellWidth + spreadWidth + SpreadWidthBuyAdjBuffer;

                buyStrike = StrikeRoundUp(synFuture - dir * buyWidth, useFloor: dir > 0);
                buyOpt = OptionCommandAccessor.GetOption(optType, buyStrike, LadderExpiryDT);
                if (buyOpt == null)
                {
                    // Add Option in Options Instrument List                   
                    string buyOptName = OptionCommandAccessor.GetOptionName(optType, buyStrike, LadderExpiryDT);
                    if (!OptionCommandAccessor.AddOption(buyOptName, out buyOpt))
                        return false;
                }

                _sl?.WriteLine($"{barData.DateTimeDT}: New Buy Strike adjusted to Spread Width {buyOpt.InstrumentName}");
            }
            

            Debug.Assert(buyOpt != null);
            #endregion



            // FAR LEG SELECTION removed � now in CreateLadderTradeLeg()



            // Generate benchmarkToTrade (spread only � 2 legs)
            var list = new List<OptionsWithQuantity>
                 {
                    new OptionsWithQuantity(sellOpt, -1 * LotSize),
                    new OptionsWithQuantity(buyOpt, 1 * LotSize)
                 };

            benchMarkToTrade = GetBenchMarkValuesPerLot(barData, synFuture, list, allOrNoneBlackScholes: true);

            int sellOffset = dir * (int)Math.Round((synFuture - sellOpt.StrikePrice) / StrikeGap);
            int buyOffset = dir * (int)Math.Round((synFuture - buyOpt.StrikePrice) / StrikeGap);

            benchMarkToTrade.InitialOffsets = new List<int?> { sellOffset, buyOffset };
            benchMarkToTrade.LegCount = 2;
            benchMarkToTrade.TradeType = tradeType;
            benchMarkToTrade.OptionsToTradeList = new List<OptionToTrade>
                 {
                     new OptionToTrade(sellOpt, OrderSide.Sell, sellOffset, 1, isSpreadLeg: true),
                     new OptionToTrade(buyOpt, OrderSide.Buy, buyOffset, 1,  isSpreadLeg: true)
                 };



            
            spreadWidth = Math.Abs(sellOpt.StrikePrice - buyOpt.StrikePrice);

            if (EnableTargetMaxLossAdj)
            {
                double marginPerSpreadLot = 45000 + 70 * spreadWidth;
                var targetMaxLoss = -1 * targetMaxLossPct * marginPerSpreadLot;

                if (targetMaxLoss.HasValue && benchMarkToTrade.MaxLoss < targetMaxLoss.Value)
                {
                    if (!AdjSpreadWidthToTargetMaxloss(benchMarkToTrade, barData, synFuture, tradeType, targetMaxLoss.Value, out TradeSetup newBenchMarkToTrade))
                        return false;            

                    benchMarkToTrade = newBenchMarkToTrade;
                }
            }



            if (targetPOP.HasValue)
            {
                
                TradeSetup newBenchMarkToTrade = null;

                //bool succ = (benchMarkToTrade.ProbOfProfitSpread > targetPOP.Value && DecSellWidthToTargetPOP(benchMarkToTrade, barData, synFuture, tradeType, targetPOP.Value, out newBenchMarkToTrade));

                bool succ = (benchMarkToTrade.ProbOfProfitSpread > targetPOP.Value && DecSellWidthToTargetPOP(benchMarkToTrade, barData, synFuture, tradeType, targetPOP.Value, out newBenchMarkToTrade)) ||
                            (benchMarkToTrade.ProbOfProfitSpread < targetPOP.Value && IncSellWidthToTargetPOP(benchMarkToTrade, barData, synFuture, tradeType, targetPOP.Value, out newBenchMarkToTrade));

                if (!succ)
                    return false;


                benchMarkToTrade = newBenchMarkToTrade;


            }

            // Far premium floor check removed � now in CreateLadderTradeLeg()
            

            // Premium floor: adjust sell/buy legs if final premiums are too thin (never reject)
            if (MinSellPremium > 0 || MinBuyPremium > 0)
            {
                ApplyPremiumFloors(benchMarkToTrade, barData, synFuture, tradeType, out benchMarkToTrade);
            }

            // Max spread width guard: narrow buy leg closer to sell if width exceeds cap (never reject)
            if (MaxSpreadWidth > 0)
            {
                var curSellOpt = benchMarkToTrade.OptionsToTradeList.FirstOrDefault(ot => ot.OrderSide == OrderSide.Sell).Option;
                var curBuyOpt = benchMarkToTrade.OptionsToTradeList.FirstOrDefault(ot => ot.OrderSide == OrderSide.Buy && ot.IsSpreadLeg).Option;
                double curWidth = Math.Abs(curSellOpt.StrikePrice - curBuyOpt.StrikePrice);

                if (curWidth > MaxSpreadWidth)
                {
                    int wDir = tradeType == TradeType.BullLadder ? 1 : -1;
                    // Move buy strike closer to sell until width <= MaxSpreadWidth
                    double targetBuyStrike = curSellOpt.StrikePrice - wDir * MaxSpreadWidth;
                    // Snap to nearest valid strike (multiple of 100, on correct side of sell)
                    double newBuyStrike = wDir > 0
                        ? Math.Ceiling(targetBuyStrike / 100.0) * 100   // PE: buy strike below sell, round up toward sell
                        : Math.Floor(targetBuyStrike / 100.0) * 100;    // CE: buy strike above sell, round down toward sell

                    var newBuyOpt = OptionCommandAccessor.GetOption(curBuyOpt.OptionType, newBuyStrike, LadderExpiryDT);
                    if (newBuyOpt != null)
                    {
                        var widthList = new List<OptionsWithQuantity>
                        {
                            new OptionsWithQuantity(curSellOpt, -1 * LotSize),
                            new OptionsWithQuantity(newBuyOpt, 1 * LotSize)
                        };
                        var newBench = GetBenchMarkValuesPerLot(barData, synFuture, widthList, allOrNoneBlackScholes: true);
                        if (newBench != null)
                        {
                            MaxWidthAdjs++;
                            int sOff = wDir * (int)((synFuture - curSellOpt.StrikePrice) / StrikeGap);
                            int bOff = wDir * (int)((synFuture - newBuyOpt.StrikePrice) / StrikeGap);
                            newBench.InitialOffsets = new List<int?> { sOff, bOff };
                            newBench.LegCount = 2;
                            newBench.TradeType = tradeType;
                            newBench.OptionsToTradeList = new List<OptionToTrade>
                            {
                                new OptionToTrade(curSellOpt, OrderSide.Sell, sOff, 1, isSpreadLeg: true),
                                new OptionToTrade(newBuyOpt, OrderSide.Buy, bOff, 1, isSpreadLeg: true)
                            };
                            benchMarkToTrade = newBench;
                            _sl?.WriteLine($"{barData.DateTimeDT}: Width adjusted from {curWidth} to {MaxSpreadWidth} (buy {curBuyOpt.StrikePrice}->{newBuyStrike})");
                        }
                    }
                }
            }

            if (!ComputeLotsAndMargin(benchMarkToTrade, out int totalSpreadLots, out double totalSpreadMargin))
                return false;


            tradeParameters.TargetIndex = -1;
            tradeParameters.TotalSpreadLots = totalSpreadLots;
            tradeParameters.TotalMargin = (barData.DateTimeDT.Date == LadderExpiryDT.Date ? ExpiryDayMarginMult : 1.0 ) * totalSpreadMargin;  // ExpiryMargin

            return true;

        }

        
        private bool AdjSpreadWidthToTargetMaxloss(TradeSetup startBenchMark, IBarData barData, double synFuture, TradeType tradeType, double targetMaxLoss, out TradeSetup benchMarkToTrade)
        {
            int dir = tradeType == TradeType.BullLadder ? 1 : -1;

            benchMarkToTrade = null;

            var sellOpt = startBenchMark.OptionsToTradeList.FirstOrDefault(ot => ot.OrderSide == OrderSide.Sell).Option;
            var buyOpt = startBenchMark.OptionsToTradeList.FirstOrDefault(ot => ot.OrderSide == OrderSide.Buy && ot.IsSpreadLeg).Option;
            // var farOpt = startBenchMark.OptionsToTradeList.FirstOrDefault(ot => !ot.IsSpreadLeg).Option;

            Debug.Assert(sellOpt.StrikePrice % 100 == 0 && buyOpt.StrikePrice % 100 == 0);

            OptionsInstrument tempBuyOpt = null;
            TradeSetup tempBenchmark = null;

            OptionsInstrument prevBuyOpt = null;
            TradeSetup prevBenchmark = null;
            double prevDiff = double.MaxValue;

            for (double tempBuyStrike = buyOpt.StrikePrice;  dir * (sellOpt.StrikePrice - tempBuyStrike) >= 100; tempBuyStrike += dir * 100)
            {
                tempBuyOpt = OptionCommandAccessor.GetOption(buyOpt.OptionType, tempBuyStrike, LadderExpiryDT);
                if (tempBuyOpt == null)
                    continue;

                var list = new List<OptionsWithQuantity>
                    {
                        new OptionsWithQuantity(sellOpt, -1 * LotSize),
                        new OptionsWithQuantity(tempBuyOpt, 1 * LotSize)
                    };

                tempBenchmark = GetBenchMarkValuesPerLot(barData, synFuture, list, allOrNoneBlackScholes: true);
                if (tempBenchmark == null)
                    continue;

                double currDiff = Math.Abs(targetMaxLoss - tempBenchmark.MaxLoss);
                              
                if (tempBenchmark.MaxLoss > targetMaxLoss)
                { 
                    if (prevBenchmark != null && currDiff >= prevDiff)
                    {
                        tempBenchmark = prevBenchmark;
                        tempBuyOpt = prevBuyOpt;
                    }

                    break;
                }
             
                prevDiff = currDiff;
                prevBenchmark = tempBenchmark;
                prevBuyOpt = tempBuyOpt;
            }

          
            if (tempBenchmark == null)
                return false;

            benchMarkToTrade = tempBenchmark;
            buyOpt = tempBuyOpt;

            if (buyOpt == null) return false;

            int sellOffset = dir * (int)((synFuture - sellOpt.StrikePrice) / StrikeGap);
            int buyOffset = dir * (int)((synFuture - buyOpt.StrikePrice) / StrikeGap);

            benchMarkToTrade.InitialOffsets = new List<int?> { sellOffset, buyOffset };
            benchMarkToTrade.LegCount = 2;
            benchMarkToTrade.TradeType = tradeType;

            benchMarkToTrade.OptionsToTradeList = new List<OptionToTrade>
                {
                    new OptionToTrade(sellOpt, OrderSide.Sell, sellOffset, 1,  isSpreadLeg: true),
                    new OptionToTrade(buyOpt, OrderSide.Buy, buyOffset, 1, isSpreadLeg: true)
                };

            return true;
        }


        private double GetPremium(OptionsInstrument opt, double synFuture, IBarData barData)
        {
            double prem = OptionCommandAccessor.GetOptionPremium(opt, lookbackTolerance: 10);
            if (double.IsNaN(prem))
                prem = BlackScholesCalculator.GetOptionBlackScholesPrice(opt, synFuture, barData.DateTimeDT, CurrentBarVIX);
            return prem;
        }


        private bool ApplyPremiumFloors(TradeSetup startBenchMark, IBarData barData, double synFuture, TradeType tradeType, out TradeSetup benchMarkToTrade)
        {
            int dir = tradeType == TradeType.BullLadder ? 1 : -1;
            benchMarkToTrade = startBenchMark;

            var sellOpt = startBenchMark.OptionsToTradeList.FirstOrDefault(ot => ot.OrderSide == OrderSide.Sell).Option;
            var buyOpt = startBenchMark.OptionsToTradeList.FirstOrDefault(ot => ot.OrderSide == OrderSide.Buy && ot.IsSpreadLeg).Option;

            double sellPrem = GetPremium(sellOpt, synFuture, barData);
            double spreadWidth = dir * (sellOpt.StrikePrice - buyOpt.StrikePrice);

            // (a) If sell premium too thin, slide both legs closer to ATM (preserving spread width)
            if (MinSellPremium > 0 && sellPrem < MinSellPremium)
            {
                OptionsInstrument tempSellOpt = null;
                OptionsInstrument tempBuyOpt = null;
                TradeSetup tempBenchmark = null;

                double tempSellStrike = sellOpt.StrikePrice;
                double tempBuyStrike = buyOpt.StrikePrice;

                while (dir * (synFuture - tempSellStrike) >= 100)
                {
                    tempSellStrike += dir * 100;
                    tempBuyStrike += dir * 100;

                    tempSellOpt = OptionCommandAccessor.GetOption(sellOpt.OptionType, tempSellStrike, LadderExpiryDT);
                    tempBuyOpt = OptionCommandAccessor.GetOption(buyOpt.OptionType, tempBuyStrike, LadderExpiryDT);
                    if (tempSellOpt == null || tempBuyOpt == null)
                        continue;

                    double tempSellPrem = GetPremium(tempSellOpt, synFuture, barData);
                    if (tempSellPrem >= MinSellPremium)
                    {
                        var list = new List<OptionsWithQuantity>
                        {
                            new OptionsWithQuantity(tempSellOpt, -1 * LotSize),
                            new OptionsWithQuantity(tempBuyOpt, 1 * LotSize)
                        };
                        tempBenchmark = GetBenchMarkValuesPerLot(barData, synFuture, list, allOrNoneBlackScholes: true);
                        if (tempBenchmark != null)
                        {
                            SellPremiumAdjs++;
                            sellOpt = tempSellOpt;
                            buyOpt = tempBuyOpt;
                            sellPrem = tempSellPrem;

                            int sOff = dir * (int)((synFuture - sellOpt.StrikePrice) / StrikeGap);
                            int bOff = dir * (int)((synFuture - buyOpt.StrikePrice) / StrikeGap);
                            tempBenchmark.InitialOffsets = new List<int?> { sOff, bOff };
                            tempBenchmark.LegCount = 2;
                            tempBenchmark.TradeType = tradeType;
                            tempBenchmark.OptionsToTradeList = new List<OptionToTrade>
                            {
                                new OptionToTrade(sellOpt, OrderSide.Sell, sOff, 1, isSpreadLeg: true),
                                new OptionToTrade(buyOpt, OrderSide.Buy, bOff, 1, isSpreadLeg: true)
                            };
                            benchMarkToTrade = tempBenchmark;
                        }
                        break;
                    }
                }

                // If still below floor after sliding, proceed anyway (never reject)
            }

            // (b) If buy premium too thin, move buy strike closer to sell (narrowing spread)
            double buyPrem = GetPremium(buyOpt, synFuture, barData);
            if (MinBuyPremium > 0 && buyPrem < MinBuyPremium)
            {
                OptionsInstrument tempBuyOpt = null;
                TradeSetup tempBenchmark = null;

                for (double tempBuyStrike = buyOpt.StrikePrice + dir * 100;
                     dir * (sellOpt.StrikePrice - tempBuyStrike) >= 100;
                     tempBuyStrike += dir * 100)
                {
                    tempBuyOpt = OptionCommandAccessor.GetOption(buyOpt.OptionType, tempBuyStrike, LadderExpiryDT);
                    if (tempBuyOpt == null)
                        continue;

                    double tempBuyPrem = GetPremium(tempBuyOpt, synFuture, barData);
                    if (tempBuyPrem >= MinBuyPremium)
                    {
                        var list = new List<OptionsWithQuantity>
                        {
                            new OptionsWithQuantity(sellOpt, -1 * LotSize),
                            new OptionsWithQuantity(tempBuyOpt, 1 * LotSize)
                        };
                        tempBenchmark = GetBenchMarkValuesPerLot(barData, synFuture, list, allOrNoneBlackScholes: true);
                        if (tempBenchmark != null)
                        {
                            BuyPremiumAdjs++;
                            buyOpt = tempBuyOpt;
                            buyPrem = tempBuyPrem;

                            int sOff = dir * (int)((synFuture - sellOpt.StrikePrice) / StrikeGap);
                            int bOff = dir * (int)((synFuture - buyOpt.StrikePrice) / StrikeGap);
                            tempBenchmark.InitialOffsets = new List<int?> { sOff, bOff };
                            tempBenchmark.LegCount = 2;
                            tempBenchmark.TradeType = tradeType;
                            tempBenchmark.OptionsToTradeList = new List<OptionToTrade>
                            {
                                new OptionToTrade(sellOpt, OrderSide.Sell, sOff, 1, isSpreadLeg: true),
                                new OptionToTrade(buyOpt, OrderSide.Buy, bOff, 1, isSpreadLeg: true)
                            };
                            benchMarkToTrade = tempBenchmark;
                        }
                        break;
                    }
                }

                // If still below floor after narrowing, proceed anyway (never reject)
            }

            return true;
        }


        private bool DecSellWidthToTargetPOP(TradeSetup startBenchMark, IBarData barData, double synFuture, TradeType tradeType, double targetPOP, out TradeSetup benchMarkToTrade)
        {
            int dir = tradeType == TradeType.BullLadder ? 1 : -1;

            benchMarkToTrade = null;

            var sellOpt = startBenchMark.OptionsToTradeList.FirstOrDefault(ot => ot.OrderSide == OrderSide.Sell).Option;
            var buyOpt = startBenchMark.OptionsToTradeList.FirstOrDefault(ot => ot.OrderSide == OrderSide.Buy && ot.IsSpreadLeg).Option;
            // var farOpt - removed (now separate trade)

            OptionType sellOptType = sellOpt.OptionType;
            OptionType buyOptType = buyOpt.OptionType;

            Debug.Assert(sellOpt.StrikePrice % 100 == 0 && buyOpt.StrikePrice % 100 == 0);

            OptionsInstrument tempBuyOpt = null;
            OptionsInstrument tempSellOpt = null;
            TradeSetup tempBenchmark = null;

            OptionsInstrument prevBuyOpt = null;
            OptionsInstrument prevSellOpt = null;
            TradeSetup prevBenchmark = null;

            double prevDiff = double.MaxValue;

          
         
            double tempBuyStrike = buyOpt.StrikePrice;
            double tempSellStrike = sellOpt.StrikePrice;

            while (dir * (synFuture - tempSellStrike) >= 100)
            {
                tempBuyOpt = OptionCommandAccessor.GetOption(buyOptType, tempBuyStrike, LadderExpiryDT);
                if (tempBuyOpt == null)
                {
                    tempBuyStrike += dir * 100;
                    tempSellStrike += dir * 100;
                    continue;
                }

                tempSellOpt = OptionCommandAccessor.GetOption(sellOptType, tempSellStrike, LadderExpiryDT);
                if (tempSellOpt == null)
                {
                    tempBuyStrike += dir * 100;
                    tempSellStrike += dir * 100;
                    continue;
                }

                var list = new List<OptionsWithQuantity>
                                 {
                                     new OptionsWithQuantity(tempSellOpt, -1 * LotSize),
                                     new OptionsWithQuantity(tempBuyOpt, 1 * LotSize)
                                 };

                tempBenchmark = GetBenchMarkValuesPerLot(barData, synFuture, list, allOrNoneBlackScholes: true);
                if (tempBenchmark == null)
                {
                    tempBuyStrike += dir * 100;
                    tempSellStrike += dir * 100;
                    continue;
                }



                double currDiff = Math.Abs(targetPOP - tempBenchmark.ProbOfProfitSpread);

                if (tempBenchmark.ProbOfProfitSpread < targetPOP)
                {
                    //if (prevBenchmark != null && currDiff >= prevDiff)
                    //{
                    //    tempBenchmark = prevBenchmark;
                    //    tempBuyOpt = prevBuyOpt;
                    //    tempSellOpt = prevSellOpt;
                    //}

                    tempBenchmark = prevBenchmark;
                    tempBuyOpt = prevBuyOpt;
                    tempSellOpt = prevSellOpt;

                    break;
                }

                prevDiff = currDiff;
                prevBenchmark = tempBenchmark;
                prevBuyOpt = tempBuyOpt;
                prevSellOpt = tempSellOpt;

                tempBuyStrike += dir * 100;
                tempSellStrike += dir * 100;
            }
           

            if (tempBenchmark == null)
                return false;

            benchMarkToTrade = tempBenchmark;
            buyOpt = tempBuyOpt;        
            sellOpt = tempSellOpt;


            if (buyOpt == null || sellOpt == null) return false;

            int sellOffset = dir * (int)((synFuture - sellOpt.StrikePrice) / StrikeGap);
            int buyOffset = dir * (int)((synFuture - buyOpt.StrikePrice) / StrikeGap);
            benchMarkToTrade.InitialOffsets = new List<int?> { sellOffset, buyOffset };
            benchMarkToTrade.LegCount = 2;
            benchMarkToTrade.TradeType = tradeType;

            benchMarkToTrade.OptionsToTradeList = new List<OptionToTrade>
                {
                    new OptionToTrade(sellOpt, OrderSide.Sell, sellOffset, 1, isSpreadLeg: true),
                    new OptionToTrade(buyOpt, OrderSide.Buy, buyOffset, 1, isSpreadLeg : true)
                };

            return true;
        }

        private bool IncSellWidthToTargetPOP(TradeSetup startBenchMark, IBarData barData, double synFuture, TradeType tradeType, double targetPOP, out TradeSetup benchMarkToTrade)
        {
            int dir = tradeType == TradeType.BullLadder ? 1 : -1;

            benchMarkToTrade = null;

            var sellOpt = startBenchMark.OptionsToTradeList.FirstOrDefault(ot => ot.OrderSide == OrderSide.Sell).Option;
            var buyOpt = startBenchMark.OptionsToTradeList.FirstOrDefault(ot => ot.OrderSide == OrderSide.Buy && ot.IsSpreadLeg).Option;


            OptionType sellOptType = sellOpt.OptionType;
            OptionType buyOptType = buyOpt.OptionType;
            Debug.Assert(sellOpt.StrikePrice % 100 == 0 && buyOpt.StrikePrice % 100 == 0);

            OptionsInstrument tempBuyOpt = null;
            OptionsInstrument tempSellOpt = null;
            TradeSetup tempBenchmark = null;

            OptionsInstrument prevBuyOpt = null;
            OptionsInstrument prevSellOpt = null;
            TradeSetup prevBenchmark = null;

            double prevDiff = double.MaxValue;



            double tempBuyStrike = buyOpt.StrikePrice;
            double tempSellStrike = sellOpt.StrikePrice;

            while (dir * (synFuture - tempSellStrike) >= 100)
            {
                tempBuyOpt = OptionCommandAccessor.GetOption(buyOptType, tempBuyStrike, LadderExpiryDT);
                if (tempBuyOpt == null)
                {
                    tempBuyStrike -= dir * 100;
                    tempSellStrike -= dir * 100;
                    continue;
                }

                tempSellOpt = OptionCommandAccessor.GetOption(sellOptType, tempSellStrike, LadderExpiryDT);
                if (tempSellOpt == null)
                {
                    tempBuyStrike -= dir * 100;
                    tempSellStrike -= dir * 100;
                    continue;
                }

                var list = new List<OptionsWithQuantity>
                                 {
                                     new OptionsWithQuantity(tempSellOpt, -1 * LotSize),
                                     new OptionsWithQuantity(tempBuyOpt, 1 * LotSize)
                                 };

                tempBenchmark = GetBenchMarkValuesPerLot(barData, synFuture, list, allOrNoneBlackScholes: true);
                if (tempBenchmark == null)
                {
                    tempBuyStrike -= dir * 100;
                    tempSellStrike -= dir * 100;
                    continue;
                }



                double currDiff = Math.Abs(targetPOP - tempBenchmark.ProbOfProfitSpread);

                if (tempBenchmark.ProbOfProfitSpread < targetPOP)
                {
                    //if (prevBenchmark != null && currDiff >= prevDiff)
                    //{
                    //    tempBenchmark = prevBenchmark;
                    //    tempBuyOpt = prevBuyOpt;
                    //    tempSellOpt = prevSellOpt;
                    //}

                    tempBenchmark = prevBenchmark;
                    tempBuyOpt = prevBuyOpt;
                    tempSellOpt = prevSellOpt;

                    break;
                }

                prevDiff = currDiff;
                prevBenchmark = tempBenchmark;
                prevBuyOpt = tempBuyOpt;
                prevSellOpt = tempSellOpt;

                tempBuyStrike -= dir * 100;
                tempSellStrike -= dir * 100;
            }


            if (tempBenchmark == null)
                return false;

            benchMarkToTrade = tempBenchmark;
            buyOpt = tempBuyOpt;
            sellOpt = tempSellOpt;


            if (buyOpt == null || sellOpt == null) return false;

            int sellOffset = dir * (int)((synFuture - sellOpt.StrikePrice) / StrikeGap);
            int buyOffset = dir * (int)((synFuture - buyOpt.StrikePrice) / StrikeGap);
            benchMarkToTrade.InitialOffsets = new List<int?> { sellOffset, buyOffset };
            benchMarkToTrade.LegCount = 2;
            benchMarkToTrade.TradeType = tradeType;

            benchMarkToTrade.OptionsToTradeList = new List<OptionToTrade>
                {
                    new OptionToTrade(sellOpt, OrderSide.Sell, sellOffset, 1, isSpreadLeg: true),
                    new OptionToTrade(buyOpt, OrderSide.Buy, buyOffset, 1, isSpreadLeg : true)
                };

            return true;
        }

               
        /* COMMENTED OUT � far leg logic moved to CreateLadderTradeLeg
        private bool AdjFarLegToTargetPremium(TradeSetup startBenchMark, IBarData barData, double synFuture, TradeType tradeType, double targetFarLegPremium, out TradeSetup benchMarkToTrade)
        {
            int dir = tradeType == TradeType.BullLadder ? 1 : -1;

            benchMarkToTrade = null;

            var sellOpt = startBenchMark.OptionsToTradeList.FirstOrDefault(ot => ot.OrderSide == OrderSide.Sell).Option;
            var buyOpt = startBenchMark.OptionsToTradeList.FirstOrDefault(ot => ot.OrderSide == OrderSide.Buy && ot.IsSpreadLeg).Option;

            Debug.Assert(sellOpt.StrikePrice % 100 == 0 && buyOpt.StrikePrice % 100 == 0);

            var newFarOpt = OptionCommandAccessor.GetOptionByPremium(farOptType, LadderExpiryDT, targetFarLegPremium, out double newFarOptPremium, out _, lookbackTolerance: 30);
            double newFarStrike = double.NaN;

            if (newFarOpt != null && newFarOpt.StrikePrice % 100 != 0)
            {
                newFarStrike = StrikeRoundUp(newFarOpt.StrikePrice, useFloor: dir < 0);
                newFarOpt = OptionCommandAccessor.GetOption(farOptType, newFarStrike, LadderExpiryDT);
                if (newFarOpt != null)
                    newFarOptPremium = OptionCommandAccessor.GetOptionPremium(newFarOpt, lookbackTolerance: 10);
            }

            if (newFarOpt == null || double.IsNaN(newFarOptPremium) || newFarOptPremium > 2.0 * targetFarLegPremium)
            {
                double TTEinDays = (LadderExpiryDT - barData.DateTimeDT).TotalDays;
                double atm_IV = BlackScholesCalculator.GetAtmIVfromIndiaVix(CurrentBarVIX, TTEinDays);
                newFarStrike = StrikeRoundUp(BlackScholesCalculator.GetStrikeFromOptionPrice(farOptType, targetFarLegPremium, synFuture, atm_IV, TTEinDays), useFloor: dir < 0);
                newFarOpt = OptionCommandAccessor.GetOption(farOptType, newFarStrike, LadderExpiryDT);

                if (newFarOpt == null)
                {
                    string farOptName = OptionCommandAccessor.GetOptionName(farOptType, newFarStrike, LadderExpiryDT);
                    if (!OptionCommandAccessor.AddOption(farOptName, out newFarOpt))
                        return false;
                }
            }

            if (newFarOpt == null)
                return false;

            var list = new List<OptionsWithQuantity>
                {
                    new OptionsWithQuantity(sellOpt, -1 * LotSize),
                    new OptionsWithQuantity(buyOpt, 1 * LotSize),
                    new OptionsWithQuantity(newFarOpt, 1 * LotSize)
                };

            var tempBenchmark = GetBenchMarkValuesPerLot(barData, synFuture, list, allOrNoneBlackScholes: true);
            if (tempBenchmark == null)
                return false;

            benchMarkToTrade = tempBenchmark;

            int sellOffset = dir * (int)((synFuture - sellOpt.StrikePrice) / StrikeGap);
            int buyOffset = dir * (int)((synFuture - buyOpt.StrikePrice) / StrikeGap);
            int farOffset = dir * (int)((newFarOpt.StrikePrice - synFuture) / StrikeGap);

            benchMarkToTrade.InitialOffsets = new List<int?> { sellOffset, buyOffset, null, farOffset };
            benchMarkToTrade.LegCount = 3;
            benchMarkToTrade.TradeType = tradeType;

            benchMarkToTrade.OptionsToTradeList = new List<OptionToTrade>
                {
                    new OptionToTrade(sellOpt, OrderSide.Sell, sellOffset, 1, isSpreadLeg: true),
                    new OptionToTrade(buyOpt, OrderSide.Buy, buyOffset, 1, isSpreadLeg: true),
                    new OptionToTrade(newFarOpt, OrderSide.Buy, farOffset, 1, isSpreadLeg: false)
                };


            return true;
        }
        */


        private bool ComputeLotsAndMargin(TradeSetup row, out int totalSpreadLots, out double totalMargin)
        {
           

            int legCount = row.LegCount;

            var sellOpt = row.OptionsToTradeList.FirstOrDefault(ot => ot.OrderSide == OrderSide.Sell).Option;
            var buyOpt = row.OptionsToTradeList.FirstOrDefault(ot => ot.OrderSide == OrderSide.Buy && ot.IsSpreadLeg).Option;
            double spreadWidth = Math.Abs(sellOpt.StrikePrice - buyOpt.StrikePrice);
            double marginPerSpreadLot = 45000 + 70 * spreadWidth;

            int tempSpreadLots = (int) Math.Round(SpreadLotsPerTrade * ExpMul);
           
            totalSpreadLots = tempSpreadLots == 0 ? 1 : tempSpreadLots;
            totalMargin = totalSpreadLots * marginPerSpreadLot;


            

            return true;
            

        }


        private double StrikeRoundUp(double strikePrice, bool useFloor)
        {

            //useFloor = !useFloor;
          
            if (useFloor)
                return Math.Floor(strikePrice / 100) * 100;
            else
                return Math.Ceiling(strikePrice / 100) * 100;
            
        }               


        public bool CreateLadderTradeLeg(TradeType ladderTradeType, IBarData barData, double synFuture,
                                          out TradeSetup ladderTradeSetup)
        {
            ladderTradeSetup = null;

            int dir = ladderTradeType == TradeType.BullLadderTrade ? 1 : -1;
            OptionType farOptType = dir > 0 ? OptionType.CE : OptionType.PE;

            double targetPremium = TargetLadderTradePremium;
            var farOpt = OptionCommandAccessor.GetOptionByPremium(farOptType, LadderExpiryDT, targetPremium, out double farOptPremium, out _, lookbackTolerance: 30);
            double farStrike = double.NaN;

            if (farOpt != null && farOpt.StrikePrice % 100 != 0)
            {
                farStrike = StrikeRoundUp(farOpt.StrikePrice, useFloor: dir < 0);
                farOpt = OptionCommandAccessor.GetOption(farOptType, farStrike, LadderExpiryDT);
                if (farOpt != null)
                    farOptPremium = OptionCommandAccessor.GetOptionPremium(farOpt, lookbackTolerance: 10);
            }

            if (farOpt == null || double.IsNaN(farOptPremium) || farOptPremium > 2.0 * targetPremium)
            {
                double TTEinDays = (LadderExpiryDT - barData.DateTimeDT).TotalDays;
                double atm_IV = BlackScholesCalculator.GetAtmIVfromIndiaVix(CurrentBarVIX, TTEinDays);
                farStrike = StrikeRoundUp(BlackScholesCalculator.GetStrikeFromOptionPrice(farOptType, targetPremium, synFuture, atm_IV, TTEinDays), useFloor: dir < 0);
                farOpt = OptionCommandAccessor.GetOption(farOptType, farStrike, LadderExpiryDT);

                if (farOpt == null)
                {
                    string farOptName = OptionCommandAccessor.GetOptionName(farOptType, farStrike, LadderExpiryDT);
                    if (!OptionCommandAccessor.AddOption(farOptName, out farOpt))
                        return false;
                }

                _sl?.WriteLine($"{barData.DateTimeDT}: LadderTrade strike selected using BlackScholes  {farOpt.InstrumentName}");
            }

            if (farOpt == null)
                return false;

            // Re-fetch premium for the final selected option
            farOptPremium = OptionCommandAccessor.GetOptionPremium(farOpt, lookbackTolerance: 10);
            if (double.IsNaN(farOptPremium))
                farOptPremium = BlackScholesCalculator.GetOptionBlackScholesPrice(farOpt, synFuture, barData.DateTimeDT, CurrentBarVIX);

            int farOffset = dir * (int)Math.Round((farOpt.StrikePrice - synFuture) / StrikeGap);

            ladderTradeSetup = new TradeSetup
            {
                LegCount = 1,
                TradeType = ladderTradeType,
                InitialOffsets = new List<int?> { farOffset },
                OptionsToTradeList = new List<OptionToTrade>
                {
                    new OptionToTrade(farOpt, OrderSide.Buy, farOffset, 1, isSpreadLeg: false)
                },
                SpreadCredit = 0,
                MaxLoss = -1 * farOptPremium * LotSize
            };

            return true;
        }

       
        public bool CreateGammaBurstLeg(TradeType gammaBurstType, IBarData barData, double synFuture,
                                         out TradeSetup gammaBurstSetup)
        {
            gammaBurstSetup = null;

            int dir = gammaBurstType == TradeType.BullGammaBurst ? 1 : -1;
            OptionType farOptType = dir > 0 ? OptionType.CE : OptionType.PE;

            // Premium-based strike selection � pick regime by DTE
            double currentExpiryDTE = (CurrentExpiryDT - barData.DateTimeDT).TotalDays;
            double targetPremium = currentExpiryDTE < GammaBurstDT0Threshold
                ? TargetGammaBurstPremiumDT0
                : TargetGammaBurstPremiumDT1;
            var gbOpt = OptionCommandAccessor.GetOptionByPremium(farOptType, CurrentExpiryDT, targetPremium, out double gbOptPremium, out _, lookbackTolerance: 30);
            double gbStrike = double.NaN;

            if (gbOpt != null && gbOpt.StrikePrice % 100 != 0)
            {
                gbStrike = StrikeRoundUp(gbOpt.StrikePrice, useFloor: dir < 0);
                gbOpt = OptionCommandAccessor.GetOption(farOptType, gbStrike, CurrentExpiryDT);
                if (gbOpt != null)
                    gbOptPremium = OptionCommandAccessor.GetOptionPremium(gbOpt, lookbackTolerance: 10);
            }

            if (gbOpt == null || double.IsNaN(gbOptPremium) || gbOptPremium > 2.0 * targetPremium)
            {
                double TTEinDays = (CurrentExpiryDT - barData.DateTimeDT).TotalDays;
                double atm_IV = BlackScholesCalculator.GetAtmIVfromIndiaVix(CurrentBarVIX, TTEinDays);
                gbStrike = StrikeRoundUp(BlackScholesCalculator.GetStrikeFromOptionPrice(farOptType, targetPremium, synFuture, atm_IV, TTEinDays), useFloor: dir < 0);
                gbOpt = OptionCommandAccessor.GetOption(farOptType, gbStrike, CurrentExpiryDT);

                // Do NOT call AddOption — it mutates the instrument collection mid-iteration
                // and causes TSExecutor index-out-of-range. Just skip if option not in data.
                if (gbOpt == null)
                    return false;

                _sl?.WriteLine($"{barData.DateTimeDT}: GammaBurst strike selected using BlackScholes  {gbOpt.InstrumentName}");
            }

            if (gbOpt == null)
                return false;

            // COMMENTED OUT — Offset-based strike selection (V2 showed worse results)
            // int offset = gammaBurstType == TradeType.BullGammaBurst ? GammaBurstOffsetBull : GammaBurstOffsetBear;
            // double targetStrike = StrikeRoundUp(synFuture + dir * offset * StrikeGap, useFloor: dir < 0);
            //
            // var gbOpt = OptionCommandAccessor.GetOption(farOptType, targetStrike, CurrentExpiryDT);
            //
            // if (gbOpt == null)
            // {
            //     string gbOptName = OptionCommandAccessor.GetOptionName(farOptType, targetStrike, CurrentExpiryDT);
            //     if (!OptionCommandAccessor.AddOption(gbOptName, out gbOpt))
            //         return false;
            // }
            //
            // double gbOptPremium;

            // Re-fetch premium for the final selected option
            gbOptPremium = OptionCommandAccessor.GetOptionPremium(gbOpt, lookbackTolerance: 10);
            if (double.IsNaN(gbOptPremium))
                gbOptPremium = BlackScholesCalculator.GetOptionBlackScholesPrice(gbOpt, synFuture, barData.DateTimeDT, CurrentBarVIX);

            int gbOffset = dir * (int)Math.Round((gbOpt.StrikePrice - synFuture) / StrikeGap);

            gammaBurstSetup = new TradeSetup
            {
                LegCount = 1,
                TradeType = gammaBurstType,
                InitialOffsets = new List<int?> { gbOffset },
                OptionsToTradeList = new List<OptionToTrade>
                {
                    new OptionToTrade(gbOpt, OrderSide.Buy, gbOffset, 1, isSpreadLeg: false)
                },
                SpreadCredit = 0,
                MaxLoss = -1 * gbOptPremium * LotSize
            };

            return true;
        }


        public double GetSyntheticFuture(IBarData barData)
        {
            OptionCommandAccessor.GetATMOptionPair(barData.Close, barData.DateTimeDT, 0, out OptionsInstrument callOptionATM, out OptionsInstrument putOptionATM);

            if (callOptionATM != null && putOptionATM != null)
            {
                double callATMPremium = OptionCommandAccessor.GetOptionPremium(callOptionATM);
                if (double.IsNaN(callATMPremium))
                    callATMPremium = BlackScholesCalculator.GetOptionBlackScholesPrice(callOptionATM, barData.Close, barData.DateTimeDT, indiaVix: CurrentBarVIX);


                double putATMPremium = OptionCommandAccessor.GetOptionPremium(putOptionATM);
                if (double.IsNaN(putATMPremium))
                    putATMPremium = BlackScholesCalculator.GetOptionBlackScholesPrice(putOptionATM, barData.Close, barData.DateTimeDT, indiaVix: CurrentBarVIX);


                double baseStrike = callOptionATM.StrikePrice;
                return baseStrike + callATMPremium - putATMPremium;
            }

            return Math.Max(barData.Close, 1e-6); // Fallback if either option is null
        }

    }
}




using QX.FinLib.Common;
using QX.FinLib.Data;
using QX.FinLib.Instrument;
using QX.OptionsBTTestApp.RD;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace QX.BackTesting.Strategies
{
    public partial class VoxLadder
    {
       
        public struct TradeParameters
        {
            public int TargetIndex;
            public int TotalSpreadLots;
            public double TotalMargin;

        }
              

        private List<TradeRecord> TradeRecords { get; set; } = new List<TradeRecord>();
        private readonly List<TradeSetup> BenchMarkTable = new List<TradeSetup>();
        private readonly List<(double, double)> EOD_Data = new List<(double, double)>();

        private static readonly List<TradeType> ThreeLegTrades = new List<TradeType> { TradeType.BullLadder, TradeType.BearLadder };
       
        private void ExecuteFreshTrade(IBarData barData,  TradeType customTradeType, string info = null)
        {

            if ((customTradeType != TradeType.BullLadder && customTradeType != TradeType.BearLadder))
                return;

            double synFuture = GetSyntheticFuture(barData);
            bool tradeSucceed = TryCustomTradeEntry(barData, synFuture, customTradeType, info);

            if (tradeSucceed) 
                RefreshCumulativePnL();
          
        }

        
        public bool TryCustomTradeEntry(IBarData barData, double synFuture, TradeType customTradeType, string info = null)
        {

            bool isTraded = false;
            
            TradeParameters tradeParameters = new TradeParameters();
            TradeSetup benchMarkToTrade = new TradeSetup();

            double? targetMaxLoss = TargetMaxLossPct;
            double? targetPOP = TargetPOP;

            try
            {
                bool proceedToExecution = CreateLadderLegs(customTradeType, barData, synFuture, out benchMarkToTrade, out tradeParameters, targetMaxLoss, targetPOP);
                isTraded = proceedToExecution ? ExecuteSelectedTrade(tradeParameters, customTradeType, barData, info, benchMarkToTrade) : false;

            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception: {ex.Message}");
                Console.WriteLine($"Stack Trace: {ex.StackTrace}");
            }

            if (isTraded)
            {
                TotalTradeCount++;             
                LastTradeDT = barData.DateTimeDT;
                LastTradedSignal = customTradeType == TradeType.BullLadder ? 1 : -1;
                ExportBenchMarkRowToCsv(benchMarkToTrade, barData);

                LogPayOffChartInfo(TradeRecords, CurrentExpiryDT, barData);
                LogPayOffChartInfo(TradeRecords, NextExpiryDT, barData);

            }


            return isTraded;

        }


        private void AddToTradeRecord(TradeRecord tradeRecord,
                                     List<(OptionToTrade optionToTrade, double entryPremium)> optionsTradedInfo,
                                     int totalSpreadLots,
                                     double transactionCost,
                                     double spreadSlippageCost,
                                     TradeSetup tradeSetup,
                                     DateTime entryTime,
                                     double underlyingAtEntry,
                                     double daystoExp,
                                     TradeType tradeType,
                                     double marginUsed,
                                     int signal)

        {
            tradeRecord.EntryTime = entryTime;
            tradeRecord.UnderlyingAtEntry = underlyingAtEntry;
            tradeRecord.VixAtEntry = CurrentBarVIX;
            tradeRecord.MostFavValue = underlyingAtEntry;
            tradeRecord.DaysToExp = daystoExp;
            tradeRecord.TradeType = tradeType;
            tradeRecord.MarginUsed = marginUsed;

            foreach (var optsInfo in optionsTradedInfo)
            {
                if (optsInfo.optionToTrade.Option == null)
                    continue; 

                tradeRecord.OptionsInfo.Add(new OptionInfo
                {
                    Option = optsInfo.optionToTrade.Option,
                    OptionType = optsInfo.optionToTrade.Option.OptionType,
                    baseLots = optsInfo.optionToTrade.BaseLots,
                    OrderSide = optsInfo.optionToTrade.OrderSide,
                    EntryPrice = optsInfo.entryPremium,
                    CurrentPrice = optsInfo.entryPremium,
                    isSpreadLeg = optsInfo.optionToTrade.IsSpreadLeg,
                    Status = Status.Open
                });
            }
            
            tradeRecord.TransactionCost = transactionCost;
            tradeRecord.SlippageCost = spreadSlippageCost; // Updated to match property name
            tradeRecord.Status = Status.Open;
            tradeRecord.BenchmarkRow = tradeSetup;
            tradeRecord.SpreadLots = totalSpreadLots;
            tradeRecord.MaxProfit = totalSpreadLots * tradeSetup.MaxProfit;
            tradeRecord.MaxLoss = totalSpreadLots * tradeSetup.MaxLoss;
            tradeRecord.SpreadCredit = totalSpreadLots * tradeSetup.SpreadCredit;
            tradeRecord.LotSize = LotSize;
            tradeRecord.StrikeGap = StrikeGap;
            tradeRecord.ExpiryDT = WorkingExpiryDT.Date + DayEndTime;           
            tradeRecord.Signal = tradeType == TradeType.BullLadder ? 1 : -1;
            tradeRecord.ProbProfitSpread = tradeSetup.ProbOfProfitSpread;

         

            var bullOpenTrades = TradeRecords.Count(t => t.Status == Status.Open && t.Signal > 0);
            var bearOpenTrades = TradeRecords.Count(t => t.Status == Status.Open && t.Signal < 0);

            tradeRecord.PyramidCount = (signal > 0 ? bullOpenTrades + 1 : -bearOpenTrades - 1);
            tradeRecord.BullXCount = bullOpenTrades - bearOpenTrades + (signal > 0 ? 1 : -1);
         

            var newTradeRecords = new List<TradeRecord>(TradeRecords) { tradeRecord }; // Defensive copy
            TradeRecords = newTradeRecords;                                            // Replace Old with new extended ! equivakent to  TradeRecords.Add(tradeRecord)
          
        }
        

        public bool ExecuteSelectedTrade(TradeParameters tradeParameters, TradeType tradeType, IBarData barData, string info = null, TradeSetup benchMarkToTrade = null)
        {
            if (benchMarkToTrade == null && tradeParameters.TargetIndex == -1)
                return false;

            int targetIndex = tradeParameters.TargetIndex;
            double marginPerTrade = tradeParameters.TotalMargin;
            int totalSpreadLots = tradeParameters.TotalSpreadLots;


            bool isTraded = false;
            var benchMarkRow = benchMarkToTrade ?? BenchMarkTable.ElementAt(targetIndex); // If not null uses provided benchMarkRow to Trade;
            var optionsToTradeList = benchMarkRow.OptionsToTradeList;

            Debug.Assert(ThreeLegTrades.Contains(tradeType));


            List<(OptionToTrade optionToTrade, double entryPremium)> optionsTradedList  = new List<(OptionToTrade, double)>();   // It has to trade info plus entry premium                                        

            foreach (var optToTrade in optionsToTradeList)
            {
                var option = optToTrade.Option;
                var orderSide = optToTrade.OrderSide;              
                var baseLots = optToTrade.BaseLots;

                double optTradedPrice;
                string msg = info;

                int orderLots = totalSpreadLots * baseLots;

                bool Entered = orderSide == OrderSide.Sell
                    ? EnterShortAtMarketX(option, orderLots, barData, out optTradedPrice, msg, optToTrade.LimitPriceToEnter, ExpiredOptionIntrinsicFill: true)
                    : EnterLongAtMarketX(option, orderLots, barData, out optTradedPrice, msg, optToTrade.LimitPriceToEnter, ExpiredOptionIntrinsicFill: true);

                Debug.Assert(Entered);
                if (!Entered)
                    throw new Exception("Failed to enter trade");

                optionsTradedList.Add((optToTrade, optTradedPrice));

            }

            isTraded = true;

            if (isTraded)
            {


                double thisTradeSlippageCost = 0;
                double thisTradeTransCost = 0;


                foreach (var optTraded in optionsTradedList)
                {

                    var orderSide = optTraded.optionToTrade.OrderSide;
                    var premium = optTraded.entryPremium;
                    var baseLots = optTraded.optionToTrade.BaseLots;

                    if (orderSide == OrderSide.Buy)
                        thisTradeTransCost += premium * baseLots * totalSpreadLots * LotSize * TransactionCostBuyPct * 0.01;
                    else
                        thisTradeTransCost += premium * baseLots * totalSpreadLots * LotSize * TransactionCostSellPct * 0.01;

                    var slippageStructure = new SlippageStructure(ExchangeIndexPair, premium, DTX);
                    double slippagePct = slippageStructure.GetSlippagePercentage();
                    thisTradeSlippageCost += premium * totalSpreadLots * baseLots * LotSize * slippagePct * 0.01;

                }


                // = buyPremiumSum * LotMultiplier * LotSize * TransactionCostBuyPct * 0.01 + sellPremiumSum * LotMultiplier * LotSize * TransactionCostSellPct * 0.01; 


                DailyTransactionCost += thisTradeTransCost;
                ExpiryTransactionCost += thisTradeTransCost;
                CycleTransactionCost += thisTradeTransCost;
                DailySlippageCost += thisTradeSlippageCost;
                ExpirySlippageCost += thisTradeSlippageCost;
                CycleSlippageCost += thisTradeSlippageCost;

                DailyMargin += marginPerTrade;

                double daysToExp = (WorkingExpiryDT - barData.DateTimeDT).TotalDays;
                int signal = RunningSignal;

                var tradeRecord = TradeRecordManager.CreateTradeRecord();
                AddToTradeRecord(tradeRecord,
                                  optionsTradedList,
                                  totalSpreadLots,
                                  thisTradeTransCost,
                                  thisTradeSlippageCost,
                                  benchMarkRow,
                                  barData.DateTimeDT,
                                  barData.Close,
                                  daysToExp,
                                  tradeType,
                                  marginPerTrade,
                                  signal);
            }

            return isTraded;
        }

      
    }
}

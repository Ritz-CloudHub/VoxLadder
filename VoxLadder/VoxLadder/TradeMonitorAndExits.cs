using MathNet.Numerics.Optimization;
using QX.BackTestingCore;
using QX.FinLib.Common;
using QX.FinLib.Data;
using QX.OptionsBTTestApp.RD;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using static QX.BackTesting.Strategies.VoxLadder;
using static System.Net.Mime.MediaTypeNames;

namespace QX.BackTesting.Strategies
{
    public partial class VoxLadder
    {
        public enum ExitType
        {
            None,
            Expired,
            SwingExit,
            StopLoss,
            StopLossEarlyExit,
            ProfitBook,
            ProfitBookEarlyExit,
            StopLossExitAbs,
            ReversalExit
            
        }       

       
        private void MonitorAndExitOpenTrades(IBarData barData)
        {
            // Independent monitors first
            MonitorLadderTrades(barData);
            MonitorGammaBurstTrades(barData);

            // Spread-only exit logic
            var openTrades = TradeRecords.Where(t => t.Status == Status.Open &&
                (t.TradeType == TradeType.BullLadder || t.TradeType == TradeType.BearLadder)).ToList();
            if (!openTrades.Any())
                return;

            var toExit = new List<TradeRecord>();

        
            foreach (var trade in openTrades)
            {

                double creditUpperbound = CreditUpperBound * SpreadLotsPerTrade;

                double credit = trade.SpreadCredit;
                if (credit > creditUpperbound)
                    credit = creditUpperbound;


                double slf;
                bool slExitCond = false;

                double swingExitPct;
                bool swingExitCond = false;


                // SIMPLIFIED - no more IsPartiallyBooked check
                // Spread SL is always based on CurrentPnL (which IS spread PnL now, no far leg)
                swingExitPct = SwingExitPct;
                slf = StopLossFactor;
                slExitCond = trade.CurrentPnL < -1 * slf * credit;



                if (slExitCond)
                {
                    trade.ExitType = ExitType.StopLoss;
                    toExit.Add(trade);
                    LogExit(trade, barData, $"STOP-LOSS)");
                    continue;
                }
               

             


                swingExitCond = Math.Abs((trade.MostFavValue - barData.Close)) * 100 / trade.MostFavValue > swingExitPct;

                if (swingExitCond)
                {
                    trade.ExitType = ExitType.SwingExit;
                    toExit.Add(trade);
                    LogExit(trade, barData, $"Reversal");
                    continue;
                }


            }


            
            
            if (toExit.Any())
            {                

                SquareOffGivenTrades(toExit, barData, "Before Expiry EXIT");
            }
                
        }
      

        /* COMMENTED OUT - Phase 4: Logic moved to MonitorLadderTrades()
        public void TryBookLadderLeg(IBarData barData)
        {

            var openTrades = TradeRecords.Where(t => t.Status == Status.Open &&
                t.BenchmarkRow != null &&
                t.TradeType != TradeType.BullGammaBurst &&
                t.TradeType != TradeType.BearGammaBurst).ToList();
            if (!openTrades.Any())
                return;

          
            var toBookFarLeg = new List<TradeRecord>();

           
            foreach (var trade in openTrades)
            {

                double daysLeftToExpiry = (trade.ExpiryDT - barData.DateTimeDT).TotalDays;

                foreach (var optInfo in trade.OptionsInfo)
                {

                    if (optInfo.isSpreadLeg || optInfo.Status == Status.Closed)
                        continue;



                   double pbPrice = Math.Max(optInfo.EntryPrice + FarLegProfitBookAdd, FarLegProfitBookMult * optInfo.EntryPrice);
                   double slPrice = Math.Max(optInfo.EntryPrice - FarLegStopLossSub, FarLegStopLossMult * optInfo.EntryPrice);

                   bool optPriceCond = optInfo.CurrentPrice > pbPrice || optInfo.CurrentPrice < slPrice;
                   bool spotPriceCond = (trade.TradeType == TradeType.BullLadder ? 1 : -1) * (barData.Close - trade.UnderlyingAtEntry) * 100 / trade.UnderlyingAtEntry > FarLegSpotMoveThreshold;
                    
                    bool bookOk = optPriceCond || spotPriceCond;

                    if (bookOk)
                    {

                        toBookFarLeg.Add(trade);
                        break;
                    }




                }
            }

            if (toBookFarLeg.Any())
                SquareOffBuyLegs(toBookFarLeg, barData);

        }
        */

        public void SettleTradesAtExchange(IBarData barData)
        {
            var CurrentExpiryTrades = TradeRecords.Where(trade => trade.ExpiryDT.Date == CurrentExpiryDT.Date && trade.Status == Status.Open).ToList();           
            SquareOffGivenTrades(CurrentExpiryTrades, barData, "Exchange Settlement", settleAtIntrinsicValue: true, ExitType.Expired);
        }

        public void SquareOffGivenTrades(List<TradeRecord> tradesToSquareOff, IBarData barData, string str ,bool settleAtIntrinsicValue = false, ExitType exitType = ExitType.None)
        {

            if (tradesToSquareOff == null || !tradesToSquareOff.Any())
                return;

            double transCost;
            double slippageCost;
            double thisTradeTransCost;
            double thisTradeSlippageCost;

            double tradedPrice;
            bool success;
            double transCostPct;

            if (dbg)
            {
                int x = 0;
            }

            foreach (var trade in tradesToSquareOff)
            {
                if (trade.Status == Status.Closed)
                    continue;

                thisTradeTransCost = 0;
                thisTradeSlippageCost = 0;

                foreach (var optInfo in trade.OptionsInfo)
                {
                    Debug.Assert(optInfo.Option != null);

                    if (optInfo.Status == Status.Closed)
                        continue;


                    if (optInfo.OrderSide == OrderSide.Buy)
                    {
                        if (settleAtIntrinsicValue)
                            success = EnterShortAtIntrinsic(optInfo.Option, optInfo.baseLots * trade.SpreadLots, barData, out tradedPrice, str);
                        else
                            success = EnterShortAtMarketX(optInfo.Option, optInfo.baseLots * trade.SpreadLots, barData, out tradedPrice, str);

                        Debug.Assert(success);
                        transCostPct = TransactionCostSellPct;

                    }
                    else
                    {
                        if (settleAtIntrinsicValue)
                            success = EnterLongAtIntrinsic(optInfo.Option, optInfo.baseLots * trade.SpreadLots, barData, out tradedPrice, str);
                        else
                            success = EnterLongAtMarketX(optInfo.Option, optInfo.baseLots * trade.SpreadLots, barData, out tradedPrice, str);

                        Debug.Assert(success);
                        transCostPct = TransactionCostBuyPct;
                    }

                    optInfo.CurrentPrice = tradedPrice;

                    optInfo.CurrentPnL = optInfo.OrderSide == OrderSide.Buy
                        ? (tradedPrice - optInfo.EntryPrice) * optInfo.baseLots * trade.SpreadLots * LotSize
                        : -1 * (tradedPrice - optInfo.EntryPrice) * optInfo.baseLots * trade.SpreadLots * LotSize;

                 

                    optInfo.Status = Status.Closed;


                    var slippageConfig = new SlippageStructure(ExchangeIndexPair, tradedPrice, DTX);
                    double spreadSlippagePct = slippageConfig.GetSlippagePercentage();
                    slippageCost = optInfo.baseLots * trade.SpreadLots * LotSize * tradedPrice * spreadSlippagePct * 0.01;
                    transCost = optInfo.baseLots * trade.SpreadLots * LotSize * tradedPrice * transCostPct * 0.01;

                    if (!settleAtIntrinsicValue)
                    {
                        thisTradeTransCost += transCost;
                        thisTradeSlippageCost += slippageCost;

                        DailyTransactionCost += transCost;
                        ExpiryTransactionCost += transCost;
                        CycleTransactionCost += transCost;

                        DailySlippageCost += slippageCost;
                        ExpirySlippageCost += slippageCost;
                        CycleSlippageCost += slippageCost;
                    }

                }


                //trade.UpdateTradeRecordPnL();

                if (exitType != ExitType.None)
                    trade.ExitType = exitType;

                trade.TransactionCost += thisTradeTransCost;
                trade.SlippageCost += thisTradeSlippageCost;

                trade.ExitTime = barData.DateTimeDT;               
                trade.UnderlyingAtExit = barData.Close;              
                trade.Status = Status.Closed;

                DailyMargin -= trade.MarginUsed;

            }
        }

        /* COMMENTED OUT - Phase 4: Logic moved to SquareOffLadderTrade()
        public void SquareOffBuyLegs(List<TradeRecord> tradesToBookBuyLeg, IBarData barData)
        {

            if (tradesToBookBuyLeg == null || !tradesToBookBuyLeg.Any())
                return;
          

            foreach (var trade in tradesToBookBuyLeg)
            {
                if (trade.Status == Status.Closed)
                    continue;

                double thisTradeTransCost = 0;
                double thisTradeSlippageCost = 0;

                foreach (var optInfo in trade.OptionsInfo)
                {
                    Debug.Assert(optInfo.Option != null);

                    if (optInfo.Status == Status.Closed || optInfo.isSpreadLeg)
                        continue;

                    Debug.Assert(EnterShortAtMarketX(optInfo.Option, optInfo.baseLots * trade.SpreadLots, barData, out double tradedPrice, message: "Buy Leg SquareOff"));
                    
                    optInfo.CurrentPrice = tradedPrice;
                    optInfo.CurrentPnL = (tradedPrice - optInfo.EntryPrice) * optInfo.baseLots * trade.SpreadLots * LotSize;                    
                    optInfo.Status = Status.Closed; // close only farbuyLeg


                    var slippageConfig = new SlippageStructure(ExchangeIndexPair, tradedPrice, DTX);
                    double spreadSlippagePct = slippageConfig.GetSlippagePercentage();
                    double slippageCost = optInfo.baseLots * trade.SpreadLots * LotSize * tradedPrice * spreadSlippagePct * 0.01;
                    double transCost = optInfo.baseLots * trade.SpreadLots * LotSize * tradedPrice * TransactionCostBuyPct * 0.01;

                    thisTradeTransCost += transCost;
                    thisTradeSlippageCost += slippageCost;

                    DailyTransactionCost += transCost;
                    ExpiryTransactionCost += transCost;
                    CycleTransactionCost += transCost;

                    DailySlippageCost += slippageCost;
                    ExpirySlippageCost += slippageCost;
                    CycleSlippageCost += slippageCost;

                }


                //trade.UpdateTradeRecordPnL();
                trade.TransactionCost += thisTradeTransCost;
                trade.SlippageCost += thisTradeSlippageCost;               
                trade.PartialExitTime = barData.DateTimeDT;
                trade.PnlAtPartialExit = trade.CurrentPnL;
                
            }

        }
        */

        private void MonitorLadderTrades(IBarData barData)
        {
            var openLT = TradeRecords.Where(t => t.Status == Status.Open &&
                (t.TradeType == TradeType.BullLadderTrade || t.TradeType == TradeType.BearLadderTrade)).ToList();

            if (!openLT.Any()) return;

            var toClose = new List<TradeRecord>();

            foreach (var trade in openLT)
            {
                var optInfo = trade.OptionsInfo[0]; // Single buy leg

                // SL check
                if (LadderTradeSLPct > 0)
                {
                    double slThreshold = optInfo.EntryPrice * (1 - LadderTradeSLPct * 0.01);
                    if (optInfo.CurrentPrice < slThreshold)
                    {
                        trade.ExitType = ExitType.StopLoss;
                        toClose.Add(trade);
                        LogExit(trade, barData, "LadderTrade SL");
                        continue;
                    }
                }

                // PB using existing far leg booking logic (price-based)
                double pbPrice = Math.Max(optInfo.EntryPrice + LadderTradePBAdd, LadderTradePBMult * optInfo.EntryPrice);
                if (optInfo.CurrentPrice > pbPrice)
                {
                    trade.ExitType = ExitType.ProfitBook;
                    toClose.Add(trade);
                    LogExit(trade, barData, "LadderTrade PB");
                    continue;
                }

                // Spot move booking (same as old FarLegSpotMoveThreshold)
                if (LadderTradeSpotThreshold > 0)
                {
                    int dir = trade.TradeType == TradeType.BullLadderTrade ? 1 : -1;
                    double spotMove = dir * (barData.Close - trade.UnderlyingAtEntry) * 100 / trade.UnderlyingAtEntry;
                    if (spotMove > LadderTradeSpotThreshold)
                    {
                        trade.ExitType = ExitType.ProfitBook;
                        toClose.Add(trade);
                        LogExit(trade, barData, "LadderTrade SpotPB");
                        continue;
                    }
                }
            }

            if (toClose.Any())
                SquareOffLadderTrade(toClose, barData);
        }

        private void SquareOffLadderTrade(List<TradeRecord> trades, IBarData barData)
        {
            foreach (var trade in trades)
            {
                if (trade.Status == Status.Closed) continue;

                var optInfo = trade.OptionsInfo[0];
                if (optInfo.Status == Status.Closed) continue;

                bool success = EnterShortAtMarketX(optInfo.Option, optInfo.baseLots * trade.SpreadLots,
                    barData, out double tradedPrice, "LadderTrade Exit");

                Debug.Assert(success);

                optInfo.CurrentPrice = tradedPrice;
                optInfo.CurrentPnL = (tradedPrice - optInfo.EntryPrice) * optInfo.baseLots * trade.SpreadLots * LotSize;
                optInfo.Status = Status.Closed;

                // Costs
                double transCost = tradedPrice * optInfo.baseLots * trade.SpreadLots * LotSize * TransactionCostSellPct * 0.01;
                var slippageConfig = new SlippageStructure(ExchangeIndexPair, tradedPrice, DTX);
                double slippageCost = tradedPrice * optInfo.baseLots * trade.SpreadLots * LotSize * slippageConfig.GetSlippagePercentage() * 0.01;

                trade.TransactionCost += transCost;
                trade.SlippageCost += slippageCost;
                DailyTransactionCost += transCost;
                ExpiryTransactionCost += transCost;
                CycleTransactionCost += transCost;
                DailySlippageCost += slippageCost;
                ExpirySlippageCost += slippageCost;
                CycleSlippageCost += slippageCost;

                trade.ExitTime = barData.DateTimeDT;
                trade.UnderlyingAtExit = barData.Close;
                trade.Status = Status.Closed;
            }
        }

                private void MonitorGammaBurstTrades(IBarData barData)
        {
            var openGammaBursts = TradeRecords.Where(t => t.Status == Status.Open &&
                (t.TradeType == TradeType.BullGammaBurst || t.TradeType == TradeType.BearGammaBurst)).ToList();

            if (!openGammaBursts.Any())
                return;

            var toExit = new List<TradeRecord>();

            foreach (var trade in openGammaBursts)
            {
                var optInfo = trade.OptionsInfo[0];

                // Determine DT0 or DT1 based on DTE at entry
                bool isDT0 = trade.DaysToExp < GammaBurstDT0Threshold;
                double slPct = isDT0 ? GammaBurstSLPctDT0 : GammaBurstSLPctDT1;
                double pbMult = isDT0 ? GammaBurstProfitBookMultipleDT0 : GammaBurstProfitBookMultipleDT1;

                // Stop loss check
                if (slPct > 0)
                {
                    double slThreshold = optInfo.EntryPrice * (1 - slPct * 0.01);
                    if (optInfo.CurrentPrice < slThreshold)
                    {
                        trade.ExitType = ExitType.StopLoss;
                        toExit.Add(trade);
                        LogExit(trade, barData, $"GammaBurst SL (DT{(isDT0 ? "0" : "1")})");
                        continue;
                    }
                }

                // Profit booking check
                if (pbMult > 0 && optInfo.CurrentPrice >= pbMult * optInfo.EntryPrice)
                {
                    trade.ExitType = ExitType.ProfitBook;
                    toExit.Add(trade);
                    LogExit(trade, barData, $"GammaBurst PB (DT{(isDT0 ? "0" : "1")})");
                }
            }

            if (toExit.Any())
                SquareOffGammaBurst(toExit, barData);
        }

        private void SquareOffGammaBurst(List<TradeRecord> trades, IBarData barData)
        {
            foreach (var trade in trades)
            {
                if (trade.Status == Status.Closed)
                    continue;

                var optInfo = trade.OptionsInfo[0];

                bool success = EnterShortAtMarketX(optInfo.Option, optInfo.baseLots * trade.SpreadLots, barData, out double tradedPrice, "GammaBurst Exit");
                Debug.Assert(success);

                optInfo.CurrentPrice = tradedPrice;
                optInfo.CurrentPnL = (tradedPrice - optInfo.EntryPrice) * optInfo.baseLots * trade.SpreadLots * LotSize;
                optInfo.Status = Status.Closed;

                // Transaction cost + slippage
                double transCost = optInfo.baseLots * trade.SpreadLots * LotSize * tradedPrice * TransactionCostSellPct * 0.01;
                var slippageConfig = new SlippageStructure(ExchangeIndexPair, tradedPrice, DTX);
                double slippagePct = slippageConfig.GetSlippagePercentage();
                double slippageCost = optInfo.baseLots * trade.SpreadLots * LotSize * tradedPrice * slippagePct * 0.01;

                trade.TransactionCost += transCost;
                trade.SlippageCost += slippageCost;

                DailyTransactionCost += transCost;
                ExpiryTransactionCost += transCost;
                CycleTransactionCost += transCost;
                DailySlippageCost += slippageCost;
                ExpirySlippageCost += slippageCost;
                CycleSlippageCost += slippageCost;

                trade.ExitTime = barData.DateTimeDT;
                trade.UnderlyingAtExit = barData.Close;
                trade.Status = Status.Closed;

                DailyMargin -= trade.MarginUsed;
            }
        }

        private void LogExit(TradeRecord trade, IBarData barData, string reason)
        {
            _sl?.WriteLine($"{barData.DateTimeDT:yyyy-MM-dd HH:mm},{trade.TradeType},{trade.SpreadLots}L,{trade.CurrentPnL:F0},{reason}");
            _sl?.Flush();
        }
    }
}
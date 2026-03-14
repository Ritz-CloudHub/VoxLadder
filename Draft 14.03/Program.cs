using OptionDataAccessorImpIAPI;
using QX.BackTesting.Strategies;
using QX.FinLib.Cache;
using QX.FinLib.Common;
using QX.FinLib.Data;
using QX.FinLib.Instrument;
using QX.FinLib.TS;
using QX.FinLib.TS.Statistics;
using QX.FinLib.TS.Strategy;
using QX.FinLib.TSOptions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;


namespace QX.OptionsBTTestApp.RD
{
    public enum ExchangeIndexPair
    {
        NSE_NIFTY,
        NSE_BANKNIFTY,
        BSE_SENSEX,
    }

    public enum ExpiryType
    {
        Weekly,
        Monthly,
    }

    public enum SeriesType
    {       
        Future,
        indiaVix
    }

  

    internal class Program
    {
        static void Main(string[] args)
        {
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
            TestPositionalSignals();
        }

        static void TestPositionalSignals()
        {
            ExchangeSegment exchangeSegment;
            int lotSize;
            int StrikeGap;             
            uint exchangeSegmentId;
            string symbol;
            string instrumentName;
            string instrumentDescription;
            double slippagePct;
            OrderExecution orderExecution;
            double startFund = 100000;
          
            ExchangeIndexPair exchangeIndexPair = ExchangeIndexPair.NSE_NIFTY;          
            ExpiryType expiryType = ExpiryType.Weekly;
           
            bool isOptunaMode = Environment.GetEnvironmentVariable("OPTUNA_MODE") == "1";
            bool requiresVix = true; // Set based on strategy or configuration
            bool requiresFutures = true; // Set based on strategy or configuration
           
            int pIndex = 0;

            slippagePct = 0;
            orderExecution = OrderExecution.CurrentBar_Close;

            switch (exchangeIndexPair)
            {
                case ExchangeIndexPair.BSE_SENSEX:
                    exchangeSegment = ExchangeSegment.BSECM;
                    exchangeSegmentId = 1;
                    symbol = "SENSEX";
                    instrumentName = "SENSEX";
                    instrumentDescription = "SENSEX";
                    lotSize = 20;
                    StrikeGap = 100;                   
                    break;

                case ExchangeIndexPair.NSE_NIFTY:
                    exchangeSegment = ExchangeSegment.NSECM;
                    exchangeSegmentId = 1;
                    symbol = "NIFTY";
                    instrumentName = "NIFTY";
                    instrumentDescription = "NIFTY";
                    lotSize = 75;
                    StrikeGap = 50;                  
                    break;

                case ExchangeIndexPair.NSE_BANKNIFTY:
                    exchangeSegment = ExchangeSegment.NSECM;
                    exchangeSegmentId = 1;
                    symbol = "BANKNIFTY";
                    instrumentName = "BANKNIFTY";
                    instrumentDescription = "BANKNIFTY";
                    lotSize = 35;
                    StrikeGap = 100;                  
                    break;

                default: throw new ArgumentException("Exchange Index is Invalid or Missing");


            }


            double minTickSize = 0.05;
            int TimeFrameinMin = 1;
            HistDataCacheManager.Instance.EnableCaching = true;
            int underlineCompressionMin = TimeFrameinMin;


            DateTime backTestingStartDate = new DateTime(2021, 01, 01, 09, 15, 00);
            DateTime backTestingEndDate = new DateTime(2026, 02, 20, 15, 29, 00);

            // string apiBaseURL = "http://182.70.127.254:91";
            // string apiBaseURL = "http://49.248.217.138:10224";
            string apiBaseURL = "http://110.227.216.55:3391";

            IOptionDataAccessor optionDataAccessorImplAPI = new OptionDataAccessorX1API(apiBaseURL, true, underlineCompressionMin);


            // Cache underlying time series
            string underlyingCacheKey = HistDataCacheManager.Instance.CreateKey(new EquityInstrument(exchangeSegment, exchangeSegmentId, symbol, instrumentName, instrumentDescription, lotSize, minTickSize), backTestingStartDate, backTestingEndDate);
            TimeDataSeries underLineTimeDataSeries = HistDataCacheManager.Instance.GetCachedTimeDataSeries(underlyingCacheKey);
            if (underLineTimeDataSeries == null)
            {
                try
                {
                    underLineTimeDataSeries = optionDataAccessorImplAPI.GetUnderlineTimeDataSeries(symbol, backTestingStartDate, backTestingEndDate);
                    if (underlineCompressionMin > 1)
                    {
                        TimeDataSeries compressedSeries = TimeDataSeries.GetMinutesBarCompression(underlineCompressionMin * 60);
                        foreach (IBarData barData in underLineTimeDataSeries)
                        {
                            compressedSeries.AddBarData(barData.DateTimeDT, barData.Open, barData.High, barData.Low, barData.Close, barData.Volume, 0);
                        }
                        underLineTimeDataSeries = compressedSeries;
                    }
                    if (underLineTimeDataSeries != null)
                    {
                        HistDataCacheManager.Instance.CacheTimeDataSeries(underlyingCacheKey, underLineTimeDataSeries);
                        Console.WriteLine($"Cached underlying time series for {symbol} with key: {underlyingCacheKey}");
                    }
                    else
                    {
                        Console.WriteLine($"Failed to fetch underlying time series for {symbol}: No data returned");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error fetching underlying time series for {symbol}: {ex.Message}");
                }
            }
            

            // Cache VIX time series (optional, based on strategy)
            TimeDataSeries vixTimeSeries = null;         
            if (requiresVix)
            {
                string vixCacheKey = HistDataCacheManager.Instance.CreateKey(new EquityInstrument(exchangeSegment, exchangeSegmentId, "INDIAVIX", "INDIAVIX", "INDIAVIX", lotSize, minTickSize), backTestingStartDate, backTestingEndDate);
                vixTimeSeries = HistDataCacheManager.Instance.GetCachedTimeDataSeries(vixCacheKey);
                if (vixTimeSeries == null)
                {
                    try
                    {
                        vixTimeSeries = OptionDataAccessorX1API.GetIndiaVIXTimeDataSeries(backTestingStartDate, backTestingEndDate, 1);
                        if (vixTimeSeries != null)
                        {
                            HistDataCacheManager.Instance.CacheTimeDataSeries(vixCacheKey, vixTimeSeries);
                            Console.WriteLine($"Cached VIX time series with key: {vixCacheKey}");
                        }
                        else
                        {
                            Console.WriteLine($"Failed to fetch VIX time series: No data returned");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error fetching VIX time series: {ex.ToString()}");
                    }
                }
                else
                {
                    Console.WriteLine($"Cache hit for VIX time series with key: {vixCacheKey}");
                }
            }

            // Cache futures time series (optional, based on strategy)
            TimeDataSeries futTimeSeries = null;           
            if (requiresFutures)
            {
                string futCacheKey = HistDataCacheManager.Instance.CreateKey(new EquityInstrument(exchangeSegment, exchangeSegmentId,"NIFTY-I", "NIFTY-I", "NIFTY-I",lotSize, minTickSize), backTestingStartDate, backTestingEndDate);
                futTimeSeries = HistDataCacheManager.Instance.GetCachedTimeDataSeries(futCacheKey);
                if (futTimeSeries == null)
                {
                    try
                    {
                        futTimeSeries = OptionDataAccessorX1API.GetFutureTimeDataSeries("NIFTY-I", backTestingStartDate, backTestingEndDate, 1);
                        if (futTimeSeries != null)
                        {
                            HistDataCacheManager.Instance.CacheTimeDataSeries(futCacheKey, futTimeSeries);
                            Console.WriteLine($"Cached futures time series for NIFTY-I with key: {futCacheKey}");
                        }
                        else
                        {
                            Console.WriteLine($"Failed to fetch futures time series for NIFTY-I: No data returned");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error fetching futures time series for NIFTY-I: {ex.ToString()}");
                    }
                }
                else
                {
                    Console.WriteLine($"Cache hit for futures time series with key: {futCacheKey}");
                }
            }



            VoxLadder strategy = new VoxLadder(
                pIndex: pIndex,
                startFund: startFund,
                exchangeIndexPair: exchangeIndexPair,
                expiryType: expiryType,            
                lotSize: lotSize,
                strikeGap: StrikeGap,                          
                backTestStartDateTime: backTestingStartDate,
                backTestEndDateTime: backTestingEndDate
            );


          

            if (requiresVix)
                strategy.SetTimeDataSeries(SeriesType.indiaVix, vixTimeSeries);
           
            if (requiresFutures)
                strategy.SetTimeDataSeries(SeriesType.Future, futTimeSeries);
            


            InstrumentBase instrument = new EquityInstrument(exchangeSegment, exchangeSegmentId, symbol, instrumentName, instrumentDescription, lotSize, minTickSize);

            StrategyConfigData strategyConfigData = new StrategyConfigData();
            strategyConfigData.InitialCapital = 100 * 100000;

            strategyConfigData.Pyramiding = PyramidingType.Disallow;
            strategyConfigData.MaxPyramidingPosition = 1;

            strategyConfigData.ContractMultiplier = 1;
            strategyConfigData.StopLossTriggerPoint = StopLossTriggerPoint.High_Low_Bar;
          
            strategyConfigData.OrderExecutionAt = orderExecution;

            strategyConfigData.SlippageType = SlippageType.Percentage;
            strategyConfigData.SlippagePercentage = slippagePct;

            bool isWeeklyExpiry = expiryType == ExpiryType.Weekly;

            TSOptionsBTExecutor tsOptionsBTExecutor;
            try
            {
                tsOptionsBTExecutor = new TSOptionsBTExecutor(strategyConfigData,
                                                              instrument,
                                                              optionDataAccessorImplAPI,
                                                              strategy,
                                                              symbol,
                                                              exchangeSegment,
                                                              StrikeGap,
                                                              backTestingStartDate,
                                                              backTestingEndDate,
                                                              preLoadedSeries: underLineTimeDataSeries,
                                                              weeklyExpiry: isWeeklyExpiry);

            }
            catch (Exception ex)
            {
               // _swCloseTradeDetails.WriteLine(ex.ToString());
                Console.WriteLine($"Pls check {ex.ToString()}");
                Console.WriteLine("Press Enter to close");
                Console.ReadLine();
                return;
            }


            bool writeCloseTradeStats = false;
            tsOptionsBTExecutor.EnableLog = true;
            Console.WriteLine("Starting Iteration....");
            tsOptionsBTExecutor.Run();


            if (writeCloseTradeStats)
            {
                var closeTradeDetailStatisticsList = tsOptionsBTExecutor.CloseTradeDetails;

                _swCloseTradeDetails = new StreamWriter("OptionsCloseTradeDetails_S.No_" + "_Time-" + DateTime.Now.ToString("dd-MM-yyyy HHmmssfff") + ".csv");

                tsOptionsBTExecutor.Log("===============  CLOSE TRADE DETAILS  ========================");
                foreach (CloseTradeDetail closeTradeDetail in closeTradeDetailStatisticsList)
                {
                    string Symbol = closeTradeDetail.SymbolName.ToString();
                    string EntryTradeDT = closeTradeDetail.EnterTradeDate.ToString();
                    string ExitTradeDT = closeTradeDetail.ExitTradeDate.ToString();
                    string Quantity = closeTradeDetail.Quantity.ToString();
                    string EntryPrice = closeTradeDetail.EntryPrice.ToString();
                    string ExitPrice = closeTradeDetail.ExitPrice.ToString();
                    string EntryText = closeTradeDetail.EntryTradeSignalType.ToString();
                    string PnL = closeTradeDetail.ProfitLoss.ToString();
                    _swCloseTradeDetails.WriteLine("Symbol : {0}, EntryTradeDT : {1}, ExitTradeDT : {2}, Quantity : {3},EntryPrice : {4}, ExitPrice : {5},EntryText : {6}, PnL : {7}", Symbol, EntryTradeDT, ExitTradeDT, Quantity, EntryPrice, ExitPrice, EntryText, PnL);
                    _swCloseTradeDetails.Flush();
                    tsOptionsBTExecutor.Log(closeTradeDetail.ToString());
                }
            }


            tsOptionsBTExecutor.Log(String.Format("============== EQUITY CURVE DATA =============="));
            EquityDataSeries erquityDataSeries = tsOptionsBTExecutor.GetEquityDataSeries();

            List<Tuple<DateTime, double>> equityCurveList = tsOptionsBTExecutor.GetEquityCurve();

            foreach (var equityCurveDataPoint in equityCurveList)
                tsOptionsBTExecutor.Log(String.Format("{0, -50}{1,-100}", equityCurveDataPoint.Item1.ToString("dd-MM-yyyy HH:mm:ss"), equityCurveDataPoint.Item2));

            PortfolioTradeStatisitcs portfolioTradeStatisitcs = tsOptionsBTExecutor.GetPortfolioTradeStatisitcs();
            Dictionary<string, double> statistics = portfolioTradeStatisitcs.GetTradeStatistics();

            tsOptionsBTExecutor.Log(String.Format("============== STATISTICS =============="));
            foreach (var statsInfo in statistics)
            {
                tsOptionsBTExecutor.Log(String.Format("{0, -50}{1,-100}", statsInfo.Key, statsInfo.Value));
            }


            // Console output for Optuna parsing (legacy block — suppressed in OptunaMode)
            if (!isOptunaMode)
            {
                Console.WriteLine("===OPTUNA_METRICS_START===");
                string[] keysToOutput = new string[] {
                    "NetProfit", "TotalProfitLoss", "MaxDrawDown", "MaxDrawDownPercentage",
                    "SharpeRatio", "WinRate", "LossRate", "ProfitFactor",
                    "TotalNumberOfTrades", "AverageTrade", "LargestLoss", "LargestProfit"
                };
                foreach (var key in keysToOutput)
                {
                    if (statistics.ContainsKey(key))
                        Console.WriteLine($"METRIC|{key}|{statistics[key]}");
                }
                Console.WriteLine("===OPTUNA_METRICS_END===");
            }


        }


        private static StreamWriter _swCloseTradeDetails;

    }
}



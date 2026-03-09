using Accord.Math.Geometry;
using QX.BackTesting.Indicators;
using QX.FinLib.Common;
using QX.FinLib.Data;
using QX.FinLib.Instrument;
using QX.FinLib.TS.Strategy;
using QX.OptionsBTTestApp.RD;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using static QX.BackTesting.Strategies.VoxLadder;

namespace QX.BackTesting.Strategies
{
    [StrategyAttribute("{B2B7FD07-225A-49FC-AE29-83DBF9E8CC09}", "VoxLadder", "VoxLadder", "VL")]

    public partial class VoxLadder : StrategyBase
    {

        #region Class & Structures

        public enum SignalMode
        {
            RuleBased,
            RegressionOnly,
            Hybrid
        }

        public enum DTE
        {
            DTE0 = 0,
            DTE1 = 1,
            DTE2 = 2,
            DTE3 = 3,
            DTE4 = 4,
            DTEW1 = 7,
            DTEW2 = 14,
            DTEW3 = 21
        }

       
        public readonly struct SlippageStructure
        {
            private readonly ExchangeIndexPair ExchangeIndexPair;
            private readonly double Premium;
            private readonly DTE DTE;
            public SlippageStructure(ExchangeIndexPair exchangeIndexPair, double premium, DTE dte)
            {
                ExchangeIndexPair = exchangeIndexPair;
                Premium = premium;
                DTE = dte;
            }
            public double GetSlippagePercentageX()
            {

                switch (ExchangeIndexPair)
                {
                    case ExchangeIndexPair.NSE_NIFTY:
                        if (Premium < 100)
                        {
                            return 0.05;
                        }
                        else if (Premium <= 200)
                        {
                            switch (DTE)
                            {
                                case DTE.DTEW1:
                                case DTE.DTEW2:
                                case DTE.DTEW3:
                                    return 0.10;
                                case DTE.DTE4:
                                    return 0.10;
                                case DTE.DTE3:
                                    return 0.10;
                                case DTE.DTE2:
                                    return 0.10;
                                case DTE.DTE1:
                                    return 0.10;
                                case DTE.DTE0:
                                    return 0.05;
                                default:
                                    return 0.10;
                            }
                        }
                        else
                        {
                            switch (DTE)
                            {
                                case DTE.DTEW1:
                                case DTE.DTEW2:
                                case DTE.DTEW3:
                                    return 0.25;
                                case DTE.DTE4:
                                    return 0.20;
                                case DTE.DTE3:
                                    return 0.15;
                                case DTE.DTE2:
                                    return 0.15;
                                case DTE.DTE1:
                                    return 0.15;
                                case DTE.DTE0:
                                    return 0.15;
                                default:
                                    return 0.15;
                            }
                        }

                    case ExchangeIndexPair.NSE_BANKNIFTY:
                    case ExchangeIndexPair.BSE_SENSEX:
                        if (Premium < 400)
                        {
                            return 0.10;
                        }
                        else if (Premium <= 600)
                        {
                            switch (DTE)
                            {
                                case DTE.DTEW1:
                                case DTE.DTEW2:
                                case DTE.DTEW3:
                                    return 0.25;
                                default:
                                    return 0.15;
                            }
                        }
                        else
                        {
                            switch (DTE)
                            {
                                case DTE.DTEW1:
                                case DTE.DTEW2:
                                case DTE.DTEW3:
                                    return 0.25;
                                default:
                                    return 0.10;
                            }
                        }

                    default:
                        return 0.20; // Default fallback
                }
            }
            public double GetSlippagePercentage()
            {
                //return Math.Max(GetSlippagePercentageX(), 0.10);
                return 2 * GetSlippagePercentageX();
            }
        }
        #endregion


        #region Fields
        private StreamWriter _sd = null; // Daily PnL
        private StreamWriter _sx = null; // Expiry PnL
        private StreamWriter _sl = null; // Important Logs
        private StreamWriter _sc = null; // cumulativePnL                                    
        private StreamWriter _sn = null; // Trend
        private StreamWriter _snt = null; // TrendValue During Trade
        private StreamWriter _sb = null; // SelectedBenchmarkRow
        private StreamWriter _st = null; // TradeRecords
        private StreamWriter _sp = null;

        private StreamWriter _slw = null; // spread width
        private StreamWriter _scs = null; // Custom Series Spot
        private StreamWriter _sds = null; // Custom Series Daily

        private StreamWriter _adxCust = null;   
        private StreamWriter _adxDaily = null;   

        private OptionsInstrumentInfoAccessor _optionsInstrumentInfoAccessor;
        private TradeRecordManager TradeRecordManager;
        private PayOffChartHelper PayOffChartHelper;
        private BlackScholesCalculator BlackScholesCalculator;
       
        private TimeDataSeries TimeDataSeriesVix = null;
        private TimeDataSeries TimeDataSeriesFut = null;
       
        
        private readonly DTE DTX;

        private readonly List<DateTime> AllExpiries = new List<DateTime>();
        #endregion


        #region File Paths
        // This gives you the folder where the .exe / .dll actually runs from (bin\Debug\net8.0\ etc.)
        private static readonly string RuntimeBaseDirectory = AppContext.BaseDirectory;

        // This gives you the project root by going up 4 levels from bin\Debug\
        // Adjust the number of ..\ if your folder structure is slightly different
        private static readonly string ProjectRoot = Path.GetFullPath(Path.Combine(RuntimeBaseDirectory, @"..\..\..\"));

        // ─────────────────────────────────────────────
        // Now define your file paths relative to project root
        // ─────────────────────────────────────────────

        private readonly string NiftyWeeklyExpiryDatesPath = Path.Combine(ProjectRoot, @"Expiry CSV\xp_NFW.csv");
        private readonly string SensexWeeklyExpiryDatesPath = Path.Combine(ProjectRoot, @"Expiry CSV\xp_SXW.csv");
        private readonly string NiftyMonthlyExpiryDatesPath = Path.Combine(ProjectRoot, @"Expiry CSV\xp_NFM.csv");
        private readonly string BankMonthlyExpiryDatesPath = Path.Combine(ProjectRoot, @"Expiry CSV\xp_BNFM.csv");
        private readonly string SignalByDatePath = Path.Combine(ProjectRoot, $@"DailySignals.csv");
        #endregion


        #region Trade Flow Control
        // Moved to StrategyParameters.cs: AtleastOneRollOverTrade, AtleastOneCurrentWeekTrade, SwitchWorkingExpiry
        #endregion


        #region Indicator
        private readonly List<double> CustomBarFutVolumes = new List<double>();
        private readonly List<double> CustomBarVixCloses = new List<double>();
        private readonly List<double> CustomBarSpotCloses = new List<double>();
        private readonly List<DateTime> CustomBarDateTimes = new List<DateTime>();
        private readonly List<double> EMAGap = new List<double>();

        private EMA EMAFast;
        private EMA EMASlow;

        private EMA VixEMAFast;
        private EMA VixEMASlow;

        private OBV OBV;
        private EMA ObvEMASlow;
        private EMA ObvEMAFast;

        private RSI RSI;

        private ADX_Custom AdxCustomSpot;
        private ADX_Daily AdxDailySpot;

        private int CustomBarCount = 0;
        private double AvgVolSMA = 0.0;

        // Moved to StrategyParameters.cs: EMAFastPeriod, EMASlowPeriod, RSIPeriod, VixEMAFastPeriod, VixEMASlowPeriod, VolSMAPeriod, ObvEMAFastPeriod, ObvEMASlowPeriod
        #endregion


        #region Variables

        // Moved to StrategyParameters.cs: BarTimePeriod, MinDaysToExpiry
        
        private double CustomBarVolume = 0.0;
        private double CurrentBarVIX = double.NaN; // Important for BlackScholes
      

        private DateTime LastTradeDT = DateTime.MinValue;
        private DateTime LastStateChangeDT = DateTime.MinValue;

        private DateTime CurrentDate = DateTime.MinValue;
        private DateTime CurrentExpiryDT = DateTime.MinValue;      
        private DateTime NextExpiryDT = DateTime.MinValue;      
        private DateTime WorkingExpiryDT = DateTime.MinValue;

        // Moved to StrategyParameters.cs: TradeStartTime, TradeEndTime
        private readonly TimeSpan DayEndTime = new TimeSpan(15, 29, 00);

        private readonly double TransactionCostBuyPct = 0.05;
        private readonly double TransactionCostSellPct = 0.15;

      
        private bool NewDayStarts = false;
        private bool NewExpiryStarts = false;      
       
        private double PrevDayCumulativePnL = 0.0;
        private double PrevExpiryCumulativePnL = 0.0;    
        private double CumulativePnL = 0.0;

       
        private double ExpiryTransactionCost = 0;
        private double DailyTransactionCost = 0;
        private double CycleTransactionCost = 0;
        private double ExpirySlippageCost = 0;
        private double DailySlippageCost = 0;
        private double CycleSlippageCost = 0;

        private int TotalTradeCount = 0;       
        private int PrevTradeCount = 0;

        private int BMCount = 0;
       
        private double PeakWeeklyMargin = 0;
        private double BlockedMargin = 0;
        private double DailyMargin = 0;
       
        private bool firstPass = true;
        private bool logDailyPayOff = false;
        private bool isDayFinalised = false;

        private TimeDataSeries TdsCustomSpot;
        private TimeDataSeries TdsDailySpot;


        #endregion


        #region Parameters Set by Program.cs
        public readonly int pIndex;
        public readonly double StartFund;
        public readonly ExchangeIndexPair ExchangeIndexPair;
        public readonly ExpiryType ExpiryType;       
        public readonly int LotSize;
        public readonly int StrikeGap;
       

        public readonly DateTime BackTestStartDateTime;
        public readonly DateTime BackTestEndDateTime;
        #endregion

        bool dbg = false;
        // Moved to StrategyParameters.cs: ExpMul

        public VoxLadder(
           int pIndex,
           double startFund,
           ExchangeIndexPair exchangeIndexPair,
           ExpiryType expiryType,          
           int lotSize,
           int strikeGap,              
           DateTime backTestStartDateTime,
           DateTime backTestEndDateTime) : base()
        {
            this.pIndex = pIndex;
            this.StartFund = startFund;
            this.ExchangeIndexPair = exchangeIndexPair;
            this.ExpiryType = expiryType;         
            this.LotSize = lotSize;
            this.StrikeGap = strikeGap;                             
            this.BackTestStartDateTime = backTestStartDateTime;
            this.BackTestEndDateTime = backTestEndDateTime;

            TradeRecords.Clear();
            Console.WriteLine($"VoxLadder Initialized");
        }

        public void SetTimeDataSeries(SeriesType Series, TimeDataSeries timeDataSeries)
        {
            if (timeDataSeries == null)
                return;

            switch (Series)
            {
                case SeriesType.indiaVix: TimeDataSeriesVix = AddTimeDataSeries("indiaVix", timeDataSeries); break;
                case SeriesType.Future: TimeDataSeriesFut = AddTimeDataSeries("Future", timeDataSeries); break;
            }
        }

        protected override void Initialize()
        {
           
            BlackScholesCalculator = new BlackScholesCalculator(this.OptionCommandAccessor);
            PayOffChartHelper = new PayOffChartHelper(this.OptionCommandAccessor, this.BlackScholesCalculator);
            TradeRecordManager = new TradeRecordManager(this.OptionCommandAccessor, this.BlackScholesCalculator);


            // For 30-min (example with BarTimePeriod = 30)
            TdsCustomSpot = TimeDataSeries.GetMinutesBarCompression(compressionSecondValue: 60 * BarTimePeriod);
            TdsDailySpot = TimeDataSeries.GetDailyBarCompression();

            AdxCustomSpot = new ADX_Custom(AdxCustomPeriod);
            AdxDailySpot = new ADX_Daily(AdxDailyPeriod);
            //Console.WriteLine($"ADX instance created - HashCode: {AdxDailySpot.GetHashCode()}");

            RSI = new RSI(null, RSIPeriod);

            EMAFast = new EMA(null, EMAFastPeriod); // Price-based on spot
            EMASlow = new EMA(null, EMASlowPeriod);

            VixEMAFast = new EMA(null, VixEMAFastPeriod);
            VixEMASlow = new EMA(null, VixEMASlowPeriod);

            OBV = new OBV();
            ObvEMASlow = new EMA(null, ObvEMASlowPeriod);           
            ObvEMAFast = new EMA(null, ObvEMAFastPeriod);

           

        }
       
        protected override void OnStateChanged()
        {
            if (StrategyState == StrategyState.ActiveMode)
            {
                _optionsInstrumentInfoAccessor = this.OptionCommandAccessor.GetOptionsInstrumentInfoAccessor();
            }
        }

        public override string GetInfoText()
        {

            return string.Empty;
        }


        #region Loading Strategy Configurations
     

        private void LoadExpiries()
        {
            try
            {
                string filePath;


                switch (ExchangeIndexPair)
                {
                    case ExchangeIndexPair.NSE_NIFTY:
                        filePath = ExpiryType == ExpiryType.Weekly ? NiftyWeeklyExpiryDatesPath : NiftyMonthlyExpiryDatesPath;
                        break;

                    case ExchangeIndexPair.NSE_BANKNIFTY:
                        filePath = BankMonthlyExpiryDatesPath;
                        break;

                    case ExchangeIndexPair.BSE_SENSEX:
                        filePath = SensexWeeklyExpiryDatesPath;
                        break;

                    default: throw new Exception($"Strategy Not Configured");

                }


                foreach (var line in File.ReadAllLines(filePath).Skip(1)) // skip header if any
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    // Parse the entire line as a date, removing any quotes
                    var dateStr = line.Trim('"');
                    if (DateTime.TryParseExact(dateStr, new[] { "yyyy-MM-dd", "dd-MM-yyyy" },
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry))
                    {
                        AllExpiries.Add(expiry);
                    }
                }

                AllExpiries.OrderBy(d => d).ToList();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception: {ex.Message}");
                Console.WriteLine($"Stack Trace: {ex.StackTrace}");
            }
        }       
        #endregion


        #region Process Strategy
        public override void ProcessStrategy()
        {
           
            if (firstPass)
            {              
            
                LoadExpiries();
                ResetOptionsMTMBasket();
                InitiateCsv();
                firstPass = false;
            }
            

            IBarData barData = TimeDataSeries[CurrentIndex];
            IBarData nextBarData = TimeDataSeries[CurrentIndex + 1];
            IBarData barDataFut = TimeDataSeriesFut[barData.DateTimeDT];

            TdsCustomSpot.AddBarData(barData);
            TdsDailySpot.AddBarData(barData);


            int idx = TimeDataSeriesVix != null ? TimeDataSeriesVix.BarDataIndexAtDateTime(barData.DateTimeDT) : -1;
            CurrentBarVIX = (idx >= 0 && TimeDataSeriesVix != null && idx < TimeDataSeriesVix.Count) ? TimeDataSeriesVix[idx].Close
                            : (~idx > 0 && TimeDataSeriesVix != null && ~idx - 1 < TimeDataSeriesVix.Count) ? TimeDataSeriesVix[~idx - 1].Close : CurrentBarVIX;

       
            CustomBarVolume += barDataFut != null ? barDataFut.Volume : 0;

            


            NewExpiryStarts = barData.DateTimeDT.Date > CurrentExpiryDT.Date;
            if (NewExpiryStarts)
            {
                CurrentExpiryDT = GetFirstExpiryAfter(barData.DateTimeDT.Date);
                NextExpiryDT = GetFirstExpiryAfter(CurrentExpiryDT.Date);
                ExpiryTransactionCost = 0;
                ExpirySlippageCost = 0;

                PeakWeeklyMargin = 0;
            }


            if (WorkingExpiryDT == DateTime.MinValue)
                WorkingExpiryDT = (CurrentExpiryDT - barData.DateTimeDT).TotalDays > MinDaysToExpiry ? CurrentExpiryDT : NextExpiryDT;

            if (LastStateChangeDT == DateTime.MinValue)
            {
                LastStateChangeDT = barData.DateTimeDT;
                LastTradeDT = barData.DateTimeDT;
            }


            NewDayStarts = barData.DateTimeDT.Date > CurrentDate;
            if (NewDayStarts)
            {
                CurrentDate = barData.DateTimeDT.Date;             
                DailyTransactionCost = 0;
                DailySlippageCost = 0;
                DailyMargin = 0;

                logDailyPayOff = true;
                isDayFinalised = false;

                ScaleMarginOnExpiry();


            }


       

            if (barData.DateTimeDT == new DateTime (2021, 01, 06, 15, 14, 00))
            {               
               dbg = true;
            }


            if (true)
            {
                var lastTrade = TradeRecords.LastOrDefault();
                int x = 0;

                if (lastTrade != null)
                {
                    if (lastTrade.OptionsInfo[0].CurrentPrice > 10000 ||
                    lastTrade.OptionsInfo[1].CurrentPrice > 10000 ||
                    lastTrade.OptionsInfo[2].CurrentPrice > 10000)
                    {
                        Console.WriteLine($"DateTime: {barData.DateTimeDT}, TradePnL: {lastTrade.CurrentPnL:F2}");
                        Console.WriteLine($"DateTime: {barData.DateTimeDT}, Option1: {lastTrade.OptionsInfo[0].CurrentPrice:F2}");
                        Console.WriteLine($"DateTime: {barData.DateTimeDT}, Option2: {lastTrade.OptionsInfo[1].CurrentPrice:F2}");
                        Console.WriteLine($"DateTime: {barData.DateTimeDT}, Option3: {lastTrade.OptionsInfo[2].CurrentPrice:F2}");
                        Console.WriteLine($"*************************************************************");
                    }
                }
                



            }

            
            TradeRecordManager.RefreshTradeRecords(TradeRecords, barData, indiaVix: CurrentBarVIX, allOrNoneBlackScholes: false);
            RefreshCumulativePnL();


            bool isTimeTradable = barData.DateTimeDT.TimeOfDay >= TradeStartTime && barData.DateTimeDT.TimeOfDay <= TradeEndTime;

            if (isTimeTradable)
            {
                MonitorAndExitOpenTrades(barData);
                TryBookSingleLeg(barData);
            }
           
            
            if (nextBarData != null && nextBarData.DateTimeDT > CurrentExpiryDT)
                SettleTradesAtExchange(barData);
           

            if ((WorkingExpiryDT - barData.DateTimeDT).TotalDays < MinDaysToExpiry)
            {
                WorkingExpiryDT = GetFirstExpiryAfter(WorkingExpiryDT.Date);
            }


            

            // Indicators Updation on Bar Completion          
            if (IsBarCompleted(BarTimePeriod, new TimeSpan(09, 15, 00)))
            {               
              
                UpdateCustomSeriesAndIndicators(barData);
                UpdateSignalState(barData);

               

                TradeType customTradeType = TradeType.None;

                bool proceedToTrade = SetCustomTradeType(barData, out customTradeType);
               

                if (proceedToTrade && isTimeTradable)
                    ExecuteFreshTrade(barData, customTradeType, info: $"New Trend".ToUpper());
              
            }


			if (nextBarData != null &&  nextBarData.DateTimeDT > CurrentExpiryDT.Date + TradeEndTime)
            {               

				//int nextExpiryBull = TradeRecords.Count(tr => tr.Status == Status.Open && tr.Signal > 0 && tr.ExpiryDT == NextExpiryDT);
				//int nextExpiryBear = TradeRecords.Count(tr => tr.Status == Status.Open && tr.Signal < 0 && tr.ExpiryDT == NextExpiryDT);

				//if (RunningSignal == 1 && nextExpiryBull == 0 || RunningSignal == -1 && nextExpiryBear == 0)
                //            {
                //                TradeType customTradeType = RunningSignal == 1 ? TradeType.BullLadder : RunningSignal == -1 ? TradeType.BearLadder : TradeType.None;
                //                ExecuteFreshTrade(barData, customTradeType, info: $"New Trend".ToUpper());
                //            }
				
			}

            if (nextBarData == null || nextBarData.DateTimeDT.Date > CurrentDate)
                FinalizeDay(barData);       

			if (nextBarData == null || nextBarData.DateTimeDT > CurrentExpiryDT)
                FinalizeExpiry(barData);
            
            if (nextBarData == null)
                ExportTradeRecordsToCsv(TradeRecords);


            if (logDailyPayOff && barData.DateTimeDT.TimeOfDay > TradeEndTime)
            {
              
               

                logDailyPayOff = false;

            }


        }
        #endregion


        #region Important Helper Methods



       

        private void UpdateCustomSeriesAndIndicators(IBarData barData)
        {

            CustomBarCount++;
            CustomBarFutVolumes.Add(CustomBarVolume);
            CustomBarVixCloses.Add(CurrentBarVIX);
            CustomBarSpotCloses.Add(barData.Close);
            CustomBarDateTimes.Add(barData.DateTimeDT);
            
            AdxCustomSpot.CalculateNew(TdsCustomSpot.LastBar);  

          
            var devBar = new BarData(barData.DateTimeDT, TdsDailySpot.LastBar.Open, TdsDailySpot.LastBar.High, TdsDailySpot.LastBar.Low, TdsDailySpot.LastBar.Close);
            AdxDailySpot.OverWriteNew(devBar); 
                                                       
            var daily = TdsDailySpot.LastBar;

            //Console.WriteLine(
            //    $"TdsDailySpot.LastBar at {barData.DateTimeDT:yyyy-MM-dd HH:mm} | " +
            //    $"Open={daily.Open:F2} | " +
            //    $"High={daily.High:F2} | " +
            //    $"Low={daily.Low:F2} | " +
            //    $"Close={daily.Close:F2} | " +
            //    $"Volume={daily.Volume} | " +
            //    $"Date={daily.DateTimeDT:yyyy-MM-dd HH:mm}"
            //);


            RSI.Update(barData.Close, barData.DateTimeDT);

            EMAFast.Update(barData.Close, barData.DateTimeDT);   // Values are Taken only After bar completion // CustomSpot
            EMASlow.Update(barData.Close, barData.DateTimeDT);

            VixEMAFast.Update(CurrentBarVIX, barData.DateTimeDT);
            VixEMASlow.Update(CurrentBarVIX, barData.DateTimeDT);

            OBV.Update(barData.Close, CustomBarVolume, barData.DateTimeDT);
            ObvEMASlow.Update(OBV.CurrentOBV, barData.DateTimeDT);
            ObvEMAFast.Update(OBV.CurrentOBV, barData.DateTimeDT);


            AvgVolSMA = CustomBarFutVolumes.Count >= VolSMAPeriod ? CustomBarFutVolumes.Skip(CustomBarFutVolumes.Count - VolSMAPeriod).Average() : CustomBarFutVolumes.Average();

            double emaGap = 100 * (EMAFast[0] - EMASlow[0]) / EMASlow[0];
            EMAGap.Add(emaGap);


            LogCustomSpotBar();  // Pass the completed bar
            CustomBarVolume = 0;

        }

        private void ScaleMarginOnExpiry()
        {

            
            var tradesToScale = TradeRecords.Where(tr => tr.Status == Status.Open)
                                            .Where(tr => tr.ExpiryDT.Date == CurrentDate)       // Trades with today's expiry
                                            .Where(tr => (int)tr.DaysToExp != 0)                // Exclude Entries today - there margin already scaled
                                            .ToList();

            if (tradesToScale == null || tradesToScale.Count == 0)
                return;

            var oldMarginUsed = tradesToScale.Sum(tr => tr.MarginUsed);

            tradesToScale.ForEach(tr => tr.MarginUsed *= ExpiryMarginScale);

            var newMarginUsed = tradesToScale.Sum(tr => tr.MarginUsed);

            DailyMargin += newMarginUsed - oldMarginUsed;
        }
      

        public void FinalizeDay(IBarData barData)
        {

            BlockedMargin += DailyMargin;
            PeakWeeklyMargin = Math.Max(PeakWeeklyMargin, BlockedMargin);

            RefreshCumulativePnL();
            double DayPnL = CumulativePnL - PrevDayCumulativePnL;
            double DayNetPnL = DayPnL - DailyTransactionCost - DailySlippageCost;
            PrevDayCumulativePnL = CumulativePnL;

           

            if (_sd != null)
            {
                _sd.WriteLine($"{CurrentDate}," +
                              $"{CumulativePnL:F2}," +
                              $"{DayNetPnL:F2}," +
                              $"{DailyTransactionCost:F2}," +
                              $"{DailySlippageCost:F2}," +                            
                              $"{DailyMargin}," +
                              $"{BlockedMargin:F2}");

                _sd.Flush();
            }



            // before Update
            var devBar = new BarData(barData.DateTimeDT, TdsDailySpot.LastBar.Open, TdsDailySpot.LastBar.High, TdsDailySpot.LastBar.Low, TdsDailySpot.LastBar.Close);
            AdxDailySpot.OverWriteNew(devBar);
            AdxDailySpot.Commit(barData.DateTimeDT.Date);
           
            LogDailySpotBar();            
            isDayFinalised = true;

        }

        public void FinalizeExpiry(IBarData barData)
        {

            Debug.Assert(isDayFinalised);

            Console.WriteLine($"DateTime: {CurrentExpiryDT}, OverallPnL: {CumulativePnL:F2}");

            RefreshCumulativePnL();
            double ExpiryPnL = CumulativePnL - PrevExpiryCumulativePnL;
            double ExpiryNetPnL = ExpiryPnL - ExpiryTransactionCost - ExpirySlippageCost;
            double TradeCount = TotalTradeCount - PrevTradeCount;
           

            if (_sx != null)
            {
                _sx.WriteLine($"{CurrentExpiryDT.Date}," +                            
                              $"{CumulativePnL:F2}," +                             
                              $"{ExpiryNetPnL:F2}," +
                              $"{ExpiryTransactionCost:F2}," +
                              $"{ExpirySlippageCost:F2}," +
                              $"{TradeCount}," +
                              $"{PeakWeeklyMargin},");                     
                             


                _sx.Flush();
            }


            PrevExpiryCumulativePnL = CumulativePnL;
            PrevTradeCount = TotalTradeCount;


           
        }
      

        public DateTime GetFirstExpiryAfter(DateTime referenceDate)
        {
            var nextExpiryDate = AllExpiries.FirstOrDefault(d => d > referenceDate);
            return nextExpiryDate.Date + DayEndTime;
        }
        
        public void ResetOptionsMTMBasket()
        {

            if (this.OptionCommandAccessor.GetOptionsMTMBasket().IsStarted)
            {
                this.OptionCommandAccessor.GetOptionsMTMBasket().Reset();
                this.OptionCommandAccessor.GetOptionsMTMBasket().Stop();

            }
            this.OptionCommandAccessor.GetOptionsMTMBasket().Start();

        }

        public void RefreshCumulativePnL()
        {       
            CumulativePnL = TradeRecords.Sum(tradeRecord => tradeRecord.CurrentPnL);
        }

        private void LogCustomSpotBar()
        {
            var bar = TdsCustomSpot.LastBar;

            if (bar == null || _scs == null)
                return;

            string line = $"{bar.DateTimeDT:yyyy-MM-dd HH:mm:ss},{bar.Open},{bar.High},{bar.Low},{bar.Close},{bar.Volume},{AdxCustomSpot[0]},{AdxCustomSpot.CurrentPlusDI},{AdxCustomSpot.CurrentMinusDI}";
            _scs.WriteLine(line);
            _scs.Flush();
        }

        private void LogDailySpotBar()
        {
            var bar = TdsDailySpot.LastBar;
            if (bar == null || _sds == null)
                return;

            string line = $"{bar.DateTimeDT:yyyy-MM-dd HH:mm:ss},{bar.Open},{bar.High},{bar.Low},{bar.Close},{bar.Volume},{AdxDailySpot.LastCommittedAdx},{AdxDailySpot.LastCommittedPlusDI},{AdxDailySpot.LastCommittedMinusDI}";
            _sds.WriteLine(line);
            _sds.Flush();
        }

        public void LogPayOffChartInfo(List<TradeRecord> tradeRecords, DateTime expiryDT, IBarData barData)
        {


            var expTradeRecords = TradeRecords.Where(tr => tr.ExpiryDT.Date == expiryDT.Date).ToList();  
            var totalPnLHitherto = TradeRecordManager.GetTotalCurrentPnL(expTradeRecords);
            var tradedOptionsWithQty = TradeRecordManager.GetNetOpenOptionsWithQty(expTradeRecords);

            if (tradedOptionsWithQty != null  && tradedOptionsWithQty.Count <= 0)
                return;



            var underLying = GetSyntheticFuture(barData);
            var linearSegments = PayOffChartHelper.GetLinearSegments(barData, totalPnLHitherto, tradedOptionsWithQty, indiaVix: CurrentBarVIX, allOrNoneBlackScholes: false);         
            var payOff = PayOffChartHelper.GetPayOffMetrics(linearSegments, barData, underLying, expiryDT, indiaVix: CurrentBarVIX);

            var dte = (expiryDT.Date - CurrentDate).TotalDays;
            var tradeCount = expTradeRecords.Count;


            //var probNxtDayLoss10Pct = PayOffChartHelper.ProbBelowGivenExpiryPnL(linearSegments, barData, underLying, expiryDT, Xdays: 1, thresholdPnL: -7500, indiaVix: CurrentBarVIX);
            //var probNxtDayLoss7Pct = PayOffChartHelper.ProbBelowGivenExpiryPnL(linearSegments, barData, underLying, expiryDT, Xdays: 1, thresholdPnL: -5000, indiaVix: CurrentBarVIX);
            //var probNxtDayLoss5Pct = PayOffChartHelper.ProbBelowGivenExpiryPnL(linearSegments, barData, underLying, expiryDT, Xdays: 1, thresholdPnL: -3500, indiaVix: CurrentBarVIX);
            //var probNxtDayLoss3Pct = PayOffChartHelper.ProbBelowGivenExpiryPnL(linearSegments, barData, underLying, expiryDT, Xdays: 1, thresholdPnL: -2000, indiaVix: CurrentBarVIX);

            string line = $"{barData.DateTimeDT}," +
                          $"{expiryDT}," +
                          $"{dte:F2}," +
                          $"{totalPnLHitherto:F2}," +
                          $"{tradeCount:F2}," +
                          $"{payOff.MaxProfit:F2}," +
                          $"{payOff.MaxLoss:F2}," +
                          $"{payOff.RiskReward:F2}," +
                          $"{payOff.POP:F2}," +
                          $"{payOff.POL:F2}," +
                          $"{payOff.PML:F2}";     


            if (_sp != null)
            {
                _sp.WriteLine(line);
                _sp.Flush();
            }
          


        }



        #endregion


     

        public void InitiateCsv()
        {

            try
            {
                string backTestLogFile = @"BackTestDebugLogs.csv";
                _sl = new StreamWriter(backTestLogFile, append: false);
                _sl.Flush();

                string tradeRecordFile = $"TradeRecords.csv";
                _st = new StreamWriter(tradeRecordFile, append: false, Encoding.UTF8);

                _st.WriteLine("EntryTime,ExitTime,PartExit," +
                              "Dir," +
                              "ExitType," +
                              "EntrySpot,ExitSpot," +
                              "SpotPoints," +
                              "EntryVix," +
                              "DTE," +
                              "TradePnL,PartXtPnL," +
                              "SprdCr,SprdPOP,MaxLoss," +
                              "Margin," +
                              "OpenBull,OpenBear,BullX," +
                              "SellEntry,BuyEntry,FarEntry," +
                              "SellExit,BuyExit,FarExit," +
                              "RetSpread,RetFarLeg," +
                              "Costs," +
                              "Offset1,Offset2,Offset3," +
                              "SellOpt,BuyOpt,FarOpt,");


                _st.Flush();




                string expiryMtmFile = @"ExpiryMTM.csv";
                _sx = new StreamWriter(expiryMtmFile, append: false);
                _sx.WriteLine("SquareOffDate,CummPnL,PnL,TransactionCost,SlippageCost,TotalTradeCount,MaxMargin");
                _sx.Flush();


                string dailyMtmFile = @"DailyMTM.csv";
                _sd = new StreamWriter(dailyMtmFile, append: false);
                _sd.WriteLine("Date,CummMTM,MTM,TransactionCost,SlippageCost,TodayMargin,TotalMargin");
                _sd.Flush();

                string trendFile = @"TrendFile.csv";
                _sn = new StreamWriter(trendFile, append: false);
                _sn.WriteLine("DateTime,Spot," +
                              "FutVolume,FutAvgVol," +
                              "EMAFast,EMASlow,EMAGap," +
                              "AdxDaily,AdxSpot," +
                              "RSI," +
                              "Vix,VixFast,VixSlow," +
                              "ObvFast,ObvSlow," +
                              "RunningSignal," +
                              "upSwing, downSwing, BearDivergence," +
                              "IsBull,IsBear,MostFavSpot," +
                              "RefSpot," +
                              "RefTime," +
                              "DeltaSwing");//,PredReturnPct,ActualReturnPct");

                _sn.Flush();


                string indicatorFile = @"IndicatorFile.csv";
                _snt = new StreamWriter(indicatorFile, append: false);
                _snt.WriteLine("DateTime,Spot," +
                              "FutVolume,FutAvgVol," +
                              "EMAFast,EMASlow,EMAGap," +
                              "AdxDaily,AdxSpot," +
                              "RSI," +
                              "Vix,VixFast,VixSlow," +
                              "ObvFast,ObvSlow," +
                              "RunningSignal," +
                              "upSwing, downSwing, BearDivergence," +
                              "IsBull,IsBear,MostFavSpot," +
                              "RefSpot," +
                              "RefTime," +
                              "DeltaSwing");//,PredReturnPct,ActualReturnPct");

                _snt.Flush();



                string benchmarkFile = $"SelectedBenchmark.csv";
                _sb = new StreamWriter(benchmarkFile, append: false, Encoding.UTF8);
                _sb.WriteLine(
                    "Time,TradeType,NetCredit,SprdPOP,MaxProfit,MaxLoss,ProbOfProfit,ProbOfLoss,ProbOfMaxProfit,ProbOfMaxLoss," +
                    "RiskRwdNetCr,RiskReward,ExpValue,EdgeFactor,LegCount," +
                    "Instrument1,Direction1,LegOffset1,BaseLots1," +
                    "Instrument2,Direction2,LegOffset2,BaseLots2," +
                    "Instrument3,Direction3,LegOffset3,BaseLots3," +
                    "Instrument4,Direction4,LegOffset4,BaseLots4," +
                    "InOffsetSell1,InOffsetBuy1,InOffsetSell2,InOffsetBuy2");
                _sb.Flush();


                _scs = new StreamWriter("CustomSpotbars.csv", append: false);
                _scs.WriteLine("DateTime,Open,High,Low,Close,Volume,ADX,DXp,DXm");

                _sds = new StreamWriter("CustomDailyBars.csv", append: false);
                _sds.WriteLine("DateTime,Open,High,Low,Close,Volume,ADX,DXp,DXm");


                string spreadWidthFile = $"SpreadWidth.csv";
                _slw = new StreamWriter(spreadWidthFile, append: false, Encoding.UTF8);
                _slw.WriteLine("DateTime,Underlying,SellOtmPct,BuyPct,FarPct,SellWidth,Buywidth,FarWidth");
                _slw.Flush();


                string payOffFile = $"PayOff_.csv";
                _sp = new StreamWriter(payOffFile, append: false, Encoding.UTF8);

                _sp.WriteLine("LogDate," +
                               "ExpiryDate," +
                               "DTE," +
                               "PnLHitherto," +
                               "OpenTrades," +
                               "MaxProfit," +
                                "MaxLoss," +
                                "RiskReward," +
                                "POP," +
                                "POL," +
                                "PML");


                _sp.Flush();


				string adxCustomFile = "AdxCustomLog.csv";
				_adxCust = new StreamWriter(adxCustomFile, append: false);
				AdxCustomSpot.SetLogger(_adxCust);


				string adxDailyFile = "AdxDailyLog.csv";
				_adxDaily = new StreamWriter(adxDailyFile, append: false);
				_adxDaily.Flush();
				AdxDailySpot.SetLogger(_adxDaily);





			}
            catch (IOException ex) when (ex.Message.Contains("being used by another process"))
            {

                Console.WriteLine($"Some Csv did not Initiate");
            }
        }

        public void ExportBenchMarkTableToCsv(List<TradeSetup> benchMarkTable)
        {
            if (benchMarkTable == null)
                return;
            BMCount++;
            string benchMarkFile = $"BenchMark_{BMCount}.csv";
            using (var writer = new StreamWriter(benchMarkFile, append: false, Encoding.UTF8))
            {
                var csvBuilder = new StringBuilder();
                // Write header
                csvBuilder.Append("TradeType,MaxProfit,MaxLoss,POPSpread,ProbOfProfit,ProbOfLoss,ProbOfMaxProfit,");
                csvBuilder.Append("ProbOfMaxLoss,RiskReward,ExpValue,EdgeFactor,LegCount,");
                csvBuilder.Append("Instrument1,Direction1,LegOffset1,BaseLots1,");
                csvBuilder.Append("Instrument2,Direction2,LegOffset2,BaseLots2,");
                csvBuilder.Append("Instrument3,Direction3,LegOffset3,BaseLots3,");
                csvBuilder.Append("Instrument4,Direction4,LegOffset4,BaseLots4,");
                csvBuilder.Append("InSell1,InBuy1,InSell2,InBuy2");
                csvBuilder.AppendLine();

                // Write data rows
                foreach (var row in benchMarkTable)
                {
                    // Build row with main fields
                    csvBuilder.Append(row.TradeType + ",");
                    csvBuilder.Append(row.MaxProfit.ToString("F2") + ",");
                    csvBuilder.Append(row.MaxLoss.ToString("F2") + ",");
                    csvBuilder.Append(row.ProbOfProfitSpread.ToString("F2") + ",");
                    csvBuilder.Append(row.ProbOfProfit.ToString("F4") + ",");
                    csvBuilder.Append(row.ProbOfLoss.ToString("F4") + ",");
                    csvBuilder.Append(row.ProbOfMaxProfit.ToString("F4") + ",");
                    csvBuilder.Append(row.ProbOfMaxLoss.ToString("F4") + ",");
                    csvBuilder.Append(row.RiskReward.ToString("F2") + ",");
                    csvBuilder.Append(row.ExpValue.ToString("F2") + ",");
                    csvBuilder.Append(row.EdgeFactor.ToString("F2") + ",");
                    csvBuilder.Append(row.LegCount + ",");

                    // Handle InitialOffset (assuming it may not exist)
                    List<int?> offsets = new List<int?>(); // Fallback: empty list
                                                           // If InitialOffset exists, uncomment and use this line instead:
                                                           // List<int?> offsets = row.InitialOffset?.Take(4).ToList() ?? new List<int?>();
                    for (int i = 0; i < 4; i++)
                    {
                        csvBuilder.Append(i < offsets.Count && offsets[i].HasValue ? offsets[i].Value.ToString() : "");
                        csvBuilder.Append(i < 3 ? "," : ""); // No comma after last offset
                    }

                    // Handle OptionsToTradeList (up to 4 legs)
                    var options = row.OptionsToTradeList?.Take(4).ToList() ?? new List<OptionToTrade>();

                    for (int i = 0; i < 4; i++)
                    {
                        if (i < options.Count)
                        {
                            csvBuilder.Append(options[i].Option + ",");
                            csvBuilder.Append(options[i].OrderSide + ",");
                            csvBuilder.Append(options[i].LegOffset + ",");
                            csvBuilder.Append(options[i].BaseLots + ",");
                        }
                        else
                        {
                            csvBuilder.Append(",,,,"); // Empty fields for missing legs
                        }
                    }
                    

                    csvBuilder.AppendLine();
                }

                // Write to file
                writer.Write(csvBuilder.ToString());
            }
        }

        public void ExportBenchMarkRowToCsv(TradeSetup benchMarkRow, IBarData barData)
        {
            if (benchMarkRow == null || barData == null || _sb == null) return;

            var rrNetCredit = benchMarkRow.SpreadCredit / Math.Abs(benchMarkRow.MaxLoss);
            
            var row = new List<string>
                            {
                                barData.DateTimeDT.ToString("yyyy-MM-dd HH:mm"),
                                benchMarkRow.TradeType.ToString(),
                                benchMarkRow.SpreadCredit.ToString("F2"),
                                 benchMarkRow.ProbOfProfitSpread.ToString("F2"),
                                benchMarkRow.MaxProfit.ToString("F2"),
                                benchMarkRow.MaxLoss.ToString("F2"),                               
                                benchMarkRow.ProbOfProfit.ToString("F2"),
                                benchMarkRow.ProbOfLoss.ToString("F2"),
                                benchMarkRow.ProbOfMaxProfit.ToString("F2"),
                                benchMarkRow.ProbOfMaxLoss.ToString("F2"),
                                rrNetCredit.ToString("F2"),
                                benchMarkRow.RiskReward.ToString("F2"),
                                benchMarkRow.ExpValue.ToString("F2"),
                                benchMarkRow.EdgeFactor.ToString("F2"),
                                benchMarkRow.LegCount.ToString()
                            };

            var options = benchMarkRow.OptionsToTradeList?.Take(4).ToList() ?? new List<OptionToTrade>();
            for (int i = 0; i < 4; i++)
            {
                if (i < options.Count)
                {
                    row.Add(options[i].Option.ToString());
                    row.Add(options[i].OrderSide.ToString());
                    row.Add(options[i].LegOffset.ToString());
                    row.Add(options[i].BaseLots.ToString());
                }
                else { row.Add(""); row.Add(""); row.Add(""); row.Add(""); }
            }

            var offsets = benchMarkRow.InitialOffsets?.Take(4).ToList() ?? new List<int?>();
            for (int i = 0; i < 4; i++)
                row.Add(i < offsets.Count && offsets[i].HasValue ? offsets[i].Value.ToString() : "");

            _sb.WriteLine(string.Join(",", row));
            _sb.Flush();               // optional – ensures data is on disk quickly
        }               

        public void ExportTradeRecordsToCsv(List<TradeRecord> tradeRecords)
        {
            if (tradeRecords == null || !tradeRecords.Any() || _st == null) 
                return;

            const int maxRetries = 5;
            const int delayMs = 100;
            int attempt = 0;


            

            while (attempt < maxRetries)
            {
                try
                {
                    foreach (var r in tradeRecords)
                    {
                        string optionsInfo = r.OptionsInfo != null
                            ? "\"" + string.Join("|", r.OptionsInfo.Select(o =>
                                $"({o.Option},{o.OptionType},{o.OrderSide},{o.baseLots}," +
                                $"{o.EntryPrice:F2},{o.CurrentPrice:F2},{o.CurrentPnL:F2},{o.Status})")) + "\""
                            : "";



                        double spotPoints = (r.Signal > 0 ? 1 : -1) * (r.UnderlyingAtExit - r.UnderlyingAtEntry);                      
                        double retSpread = r.OptionsInfo[0].CurrentPnL + r.OptionsInfo[1].CurrentPnL;
                        double retFarLeg = r.OptionsInfo[2].CurrentPnL;
                        double cost = r.TransactionCost + r.SlippageCost;

                        _st.WriteLine(
                            $"{r.EntryTime:yyyy-MM-dd HH:mm}," +
                            $"{r.ExitTime:yyyy-MM-dd HH:mm}," +
                            $"{r.PartialExitTime:yyyy-MM-dd HH:mm}," +
                            $"{(r.TradeType == TradeType.BullLadder ? "Bull" : "Bear")}," +
                            $"{r.ExitType}," +
                            $"{r.UnderlyingAtEntry:F2}," +
                            $"{r.UnderlyingAtExit:F2}," +
                            $"{spotPoints:F2}," +
                            $"{r.VixAtEntry:F2}," +                           
                            $"{r.DaysToExp:F2}," +
                            $"{r.CurrentPnL:F2}," +
                            $"{r.PnlAtPartialExit:F2}," +
                            $"{r.SpreadCredit:F2}," +
                            $"{r.ProbProfitSpread:F2}," +
                            $"{r.MaxLoss:F2}," +
                            $"{r.MarginUsed:F2}," +
                            $"{(r.Signal > 0 ? r.PyramidCount : "")}," +
                            $"{(r.Signal < 0 ? -r.PyramidCount : "")}," +
                            $"{r.BullXCount}," +
                            $"{r.OptionsInfo[0].EntryPrice:F2},{r.OptionsInfo[1].EntryPrice:F2},{r.OptionsInfo[2].EntryPrice:F2}," +
                            $"{r.OptionsInfo[0].CurrentPrice:F2},{r.OptionsInfo[1].CurrentPrice:F2},{r.OptionsInfo[2].CurrentPrice:F2}," +
                            $"{retSpread:F2}," +
                            $"{retFarLeg:F2}," +
                            $"{cost:F2}," +
                            $"{r.BenchmarkRow.InitialOffsets[0] * StrikeGap},{r.BenchmarkRow.InitialOffsets[1] * StrikeGap},{r.BenchmarkRow.InitialOffsets[3] * StrikeGap}," +
                            $"{r.BenchmarkRow.OptionsToTradeList[0].Option.InstrumentName}," +
                            $"{r.BenchmarkRow.OptionsToTradeList[1].Option.InstrumentName}," +
                            $"{r.BenchmarkRow.OptionsToTradeList[2].Option.InstrumentName}");
                    }

                    _st.Flush();
                    return;               // success → exit retry loop
                }
                catch (IOException ex) when (ex.Message.Contains("being used by another process"))
                {
                    attempt++;
                    if (attempt >= maxRetries)
                    {
                        Console.WriteLine($"Failed to write TradeRecords after {maxRetries} attempts.");
                        return;
                    }
                    Thread.Sleep(delayMs);
                }
            }
        }
       

        //public void ExportTradeRecordsToCsvX(List<TradeRecord> tradeRecords)
        //{
        //    if (tradeRecords == null || !tradeRecords.Any() || _st == null) return;




        //    const int maxRetries = 5;
        //    const int delayMs = 100;
        //    int attempt = 0;

        //    while (attempt < maxRetries)
        //    {
        //        try
        //        {
        //            foreach (var r in tradeRecords)
        //            {
        //                string optionsInfo = r.OptionsInfo != null
        //                    ? "\"" + string.Join("|", r.OptionsInfo.Select(o =>
        //                        $"({o.Option},{o.OptionType},{o.OrderSide},{o.baseLots}," +
        //                        $"{o.EntryPrice:F2},{o.CurrentPrice:F2},{o.CurrentPnL:F2},{o.Status})")) + "\""
        //                    : "";

        //                _st.WriteLine(
        //                    $"{r.EntryTime:yyyy-MM-dd HH:mm:ss}," +
        //                    $"{r.ExitTime:yyyy-MM-dd HH:mm:ss}," +
        //                    $"{r.ExitType}," +
        //                    $"{r.UnderlyingAtEntry:F2},{r.UnderlyingAtExit:F2},{r.VixAtEntry:F2},{r.TradeType}," +
        //                    $"{r.DaysToExp:F2},{r.MarginUsed:F2}," +
        //                    $"{r.CurrentPnL:F2},{r.CyclePnLAtExit:F2},{r.TransactionCost:F2}," +
        //                    $"{r.SlippageCost:F2},{r.Status},{r.SpreadLots},{r.NetCredit:F2},{r.MaxProfit:F2}," +
        //                    $"{r.MaxLoss:F2},{r.BullTradeSkew},{r.ExpiryDT},{r.Signal}," +
        //                    $"{r.OptionsInfo[0].EntryPrice:F2},{r.OptionsInfo[1].EntryPrice:F2},{r.OptionsInfo[2].EntryPrice:F2}," +
        //                    $"{r.OptionsInfo[0].CurrentPrice:F2},{r.OptionsInfo[1].CurrentPrice:F2},{r.OptionsInfo[2].CurrentPrice:F2}," +
        //                    $"{r.BenchmarkRow.InitialOffsets[0]},{r.BenchmarkRow.InitialOffsets[1]},{r.BenchmarkRow.InitialOffsets[3]}," +
        //                    $"{optionsInfo}");
        //            }
        //            _st.Flush();
        //            return;               // success → exit retry loop
        //        }
        //        catch (IOException ex) when (ex.Message.Contains("being used by another process"))
        //        {
        //            attempt++;
        //            if (attempt >= maxRetries)
        //            {
        //                Console.WriteLine($"Failed to write TradeRecords after {maxRetries} attempts.");
        //                return;
        //            }
        //            Thread.Sleep(delayMs);
        //        }
        //    }
        //}

        public override void OnPositionChange(int marketPosition)
        {

        }

        public override void OnClean()
        {

        }
    }
}


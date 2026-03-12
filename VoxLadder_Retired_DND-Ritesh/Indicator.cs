using QX.FinLib.Data;
using QX.FinLib.Data.TI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QX.BackTesting.Indicators
{
    public class EMA
    {
        private readonly int _period;
        private readonly double _multiplier;
        private readonly List<double> _emaValues;
        private readonly List<DateTime> _emaDateTimes;
        private double _currentEMA;
        private double _sum;
        private int _barCount;
        private readonly TimeDataSeries _timeDataSeries;

        public EMA(TimeDataSeries timeDataSeries, int period)
        {
          
            if (period <= 0) 
                throw new ArgumentException("Period must be positive.");

            _timeDataSeries = timeDataSeries;
            _period = period;
            _multiplier = 2.0 / (period + 1.0);
            _emaValues = new List<double>();
            _emaDateTimes = new List<DateTime>();
            _currentEMA = double.NaN;
            _sum = 0.0;
            _barCount = 0;
        }

        public void CalculateHistory()
        {
            // Need to pass Time Data Series for Historical Calculations

            if (_timeDataSeries == null)
                return;

            _emaValues.Clear();
            _emaDateTimes.Clear();
            _sum = 0.0;
            _barCount = 0;
            _currentEMA = double.NaN;

            for (int i = 0; i < _timeDataSeries.Count; i++)
            {
                double close = _timeDataSeries[i].Close;
                DateTime barTime = _timeDataSeries[i].DateTimeDT;

                _sum += close;
                _barCount++;

                if (_barCount < _period)
                {
                    _emaValues.Add(double.NaN);
                    _emaDateTimes.Add(barTime);
                }
                else if (_barCount == _period)
                {
                    _currentEMA = _sum / _period;
                    _emaValues.Add(_currentEMA);
                    _emaDateTimes.Add(barTime);
                }
                else
                {
                    _currentEMA = (close * _multiplier) + (_currentEMA * (1 - _multiplier));
                    _emaValues.Add(_currentEMA);
                    _emaDateTimes.Add(barTime);
                }
            }
        } // First Calculation On historical data

        public void Update(IBarData newBar)
        {
            if (newBar == null) 
                return;

            double close = newBar.Close;
            DateTime barTime = newBar.DateTimeDT;

            _sum += close;
            _barCount++;

            if (_barCount < _period)
            {
                _emaValues.Add(double.NaN);
                _emaDateTimes.Add(barTime);
            }
            else if (_barCount == _period)
            {
                _currentEMA = _sum / _period;
                _emaValues.Add(_currentEMA);
                _emaDateTimes.Add(barTime);
            }
            else
            {
                _currentEMA = (close * _multiplier) + (_currentEMA * (1 - _multiplier));
                _emaValues.Add(_currentEMA);
                _emaDateTimes.Add(barTime);
            }
        }

        public void Update(double close, DateTime updateTime)
        {
            _sum += close;
            _barCount++;

            if (_barCount < _period)
            {
                _emaValues.Add(double.NaN);
                _emaDateTimes.Add(updateTime);
            }
            else if (_barCount == _period)
            {
                _currentEMA = _sum / _period;
                _emaValues.Add(_currentEMA);
                _emaDateTimes.Add(updateTime);
            }
            else
            {
                _currentEMA = (close * _multiplier) + (_currentEMA * (1 - _multiplier));
                _emaValues.Add(_currentEMA);
                _emaDateTimes.Add(updateTime);
            }
        }

        public double this[int index]
        {
            get
            {
                if (index < 0 || index >= _emaValues.Count) 
                    return double.NaN;

                return _emaValues[_emaValues.Count - 1 - index];
            }
        }

        public DateTime GetEMADateTime(int index)
        {
            if (index < 0 || index >= _emaDateTimes.Count) 
                return DateTime.MinValue;

            return _emaDateTimes[_emaDateTimes.Count - 1 - index];
        }

        public List<DateTime> GetEMADateTimeList() => new List<DateTime>(_emaDateTimes);

        /// <summary>
        /// Returns the lowest OBV value in the last 'lookback' bars (inclusive of current bar)
        /// </summary>
        public double Lowest(int lookback)
        {
            if (lookback <= 0 || _emaValues.Count == 0)
                return double.NaN;

            int startIdx = Math.Max(0, _emaValues.Count - lookback);
            double min = _emaValues[startIdx];

            for (int i = startIdx + 1; i < _emaValues.Count; i++)
            {
                if (_emaValues[i] < min)
                    min = _emaValues[i];
            }
            return min;
        }

        /// <summary>
        /// Returns the highest OBV value in the last 'lookback' bars (inclusive of current bar)
        /// </summary>
        public double Highest(int lookback)
        {
            if (lookback <= 0 || _emaValues.Count == 0)
                return double.NaN;

            int startIdx = Math.Max(0, _emaValues.Count - lookback);
            double max = _emaValues[startIdx];

            for (int i = startIdx + 1; i < _emaValues.Count; i++)
            {
                if (_emaValues[i] > max)
                    max = _emaValues[i];
            }
            return max;
        }

        public double CurrentEMA => _emaValues.Count > 0 ? _emaValues[_emaValues.Count - 1] : double.NaN;

        public double PreviousEMA => _emaValues.Count >= _period + 1 ? _emaValues[_emaValues.Count - 2] : double.NaN;

        public DateTime CurrentEMADateTime => _emaDateTimes.Count > 0 ? _emaDateTimes[_emaDateTimes.Count - 1] : DateTime.MinValue;

        public int Count => _emaValues.Count;
    }

    public class RSI
    {
        private readonly int _period;
        private readonly List<double> _rsiValues;
        private readonly List<DateTime> _rsiDateTimes;   // <-- ADDED
        private readonly List<double> _closes;
        private double _avgGain;
        private double _avgLoss;
        private double _previousClose;
        private readonly TimeDataSeries _timeDataSeries;

        public RSI(TimeDataSeries timeDataSeries, int period)
        {
            
            if (period <= 0)
                throw new ArgumentException("Period must be positive.");
            _timeDataSeries = timeDataSeries;
            _period = period;
            _rsiValues = new List<double>();
            _rsiDateTimes = new List<DateTime>();        // <-- ADDED
            _closes = new List<double>();
            _avgGain = 0.0;
            _avgLoss = 0.0;
            _previousClose = double.NaN;
        }

        public void Calculathistory()
        {

            // Need to pass Time Data Series for Historical Calculations

            if (_timeDataSeries == null)
                return;

            _closes.Clear();
            for (int i = 0; i < _timeDataSeries.Count; i++)
            {
                _closes.Add(_timeDataSeries[i].Close);
            }
            _rsiValues.Clear();
            _rsiDateTimes.Clear();                       // <-- ADDED
            _avgGain = 0.0;
            _avgLoss = 0.0;
            _previousClose = double.NaN;

            if (_closes.Count < _period + 1)
            {
                for (int i = 0; i < _closes.Count; i++)
                {
                    _rsiValues.Add(double.NaN);
                    _rsiDateTimes.Add(_timeDataSeries[i].DateTimeDT);  // <-- ADDED
                }
                if (_closes.Count > 0)
                    _previousClose = _closes[_closes.Count - 1];
                return;
            }

            // Add NaN for first _period bars
            for (int i = 0; i < _period; i++)
            {
                _rsiValues.Add(double.NaN);
                _rsiDateTimes.Add(_timeDataSeries[i].DateTimeDT);  // <-- ADDED
            }

            // Calculate initial average gain and loss
            double sumGain = 0.0;
            double sumLoss = 0.0;
            for (int j = 1; j <= _period; j++)
            {
                double change = _closes[j] - _closes[j - 1];
                sumGain += Math.Max(change, 0.0);
                sumLoss += Math.Max(-change, 0.0);
            }
            _avgGain = sumGain / _period;
            _avgLoss = sumLoss / _period;
            double rs = _avgLoss == 0 ? double.PositiveInfinity : _avgGain / _avgLoss;
            double rsi = _avgLoss == 0 ? 100.0 : 100.0 - (100.0 / (1.0 + rs));
            _rsiValues.Add(rsi);
            _rsiDateTimes.Add(_timeDataSeries[_period].DateTimeDT);  // <-- ADDED
            _previousClose = _closes[_period];

            // Calculate subsequent RSI values
            for (int i = _period + 1; i < _closes.Count; i++)
            {
                double change = _closes[i] - _closes[i - 1];
                double gain = Math.Max(change, 0.0);
                double loss = Math.Max(-change, 0.0);
                _avgGain = ((_avgGain * (_period - 1)) + gain) / _period;
                _avgLoss = ((_avgLoss * (_period - 1)) + loss) / _period;
                rs = _avgLoss == 0 ? double.PositiveInfinity : _avgGain / _avgLoss;
                rsi = _avgLoss == 0 ? 100.0 : 100.0 - (100.0 / (1.0 + rs));
                _rsiValues.Add(rsi);
                _rsiDateTimes.Add(_timeDataSeries[i].DateTimeDT);  // <-- ADDED
                _previousClose = _closes[i];
            }
        }

        public void Update(IBarData newBar)
        {
            if (newBar == null)
                return;

            double close = newBar.Close;
            _closes.Add(close);

            DateTime barTime = newBar.DateTimeDT;       // <-- ADDED

            if (_closes.Count <= _period)
            {
                _rsiValues.Add(double.NaN);
                _rsiDateTimes.Add(barTime);              // <-- ADDED
            }
            else if (_closes.Count == _period + 1)
            {
                double sumGain = 0.0;
                double sumLoss = 0.0;
                for (int j = 1; j <= _period; j++)
                {
                    double change = _closes[j] - _closes[j - 1];
                    sumGain += Math.Max(change, 0.0);
                    sumLoss += Math.Max(-change, 0.0);
                }
                _avgGain = sumGain / _period;
                _avgLoss = sumLoss / _period;
                double rs = _avgLoss == 0 ? double.PositiveInfinity : _avgGain / _avgLoss;
                double rsi = _avgLoss == 0 ? 100.0 : 100.0 - (100.0 / (1.0 + rs));
                _rsiValues.Add(rsi);
                _rsiDateTimes.Add(barTime);              // <-- ADDED
                _previousClose = close;
            }
            else
            {
                double change = close - _previousClose;
                double gain = Math.Max(change, 0.0);
                double loss = Math.Max(-change, 0.0);
                _avgGain = ((_avgGain * (_period - 1)) + gain) / _period;
                _avgLoss = ((_avgLoss * (_period - 1)) + loss) / _period;
                double rs = _avgLoss == 0 ? double.PositiveInfinity : _avgGain / _avgLoss;
                double rsi = _avgLoss == 0 ? 100.0 : 100.0 - (100.0 / (1.0 + rs));
                _rsiValues.Add(rsi);
                _rsiDateTimes.Add(barTime);              // <-- ADDED
                _previousClose = close;
            }
        }


        public void Update(double close, DateTime updateTime)
        {
            
            
            _closes.Add(close);

           
            if (_closes.Count <= _period)
            {
                _rsiValues.Add(double.NaN);
                _rsiDateTimes.Add(updateTime);              // <-- ADDED
            }
            else if (_closes.Count == _period + 1)
            {
                double sumGain = 0.0;
                double sumLoss = 0.0;
                for (int j = 1; j <= _period; j++)
                {
                    double change = _closes[j] - _closes[j - 1];
                    sumGain += Math.Max(change, 0.0);
                    sumLoss += Math.Max(-change, 0.0);
                }
                _avgGain = sumGain / _period;
                _avgLoss = sumLoss / _period;
                double rs = _avgLoss == 0 ? double.PositiveInfinity : _avgGain / _avgLoss;
                double rsi = _avgLoss == 0 ? 100.0 : 100.0 - (100.0 / (1.0 + rs));
                _rsiValues.Add(rsi);
                _rsiDateTimes.Add(updateTime);              // <-- ADDED
                _previousClose = close;
            }
            else
            {
                double change = close - _previousClose;
                double gain = Math.Max(change, 0.0);
                double loss = Math.Max(-change, 0.0);
                _avgGain = ((_avgGain * (_period - 1)) + gain) / _period;
                _avgLoss = ((_avgLoss * (_period - 1)) + loss) / _period;
                double rs = _avgLoss == 0 ? double.PositiveInfinity : _avgGain / _avgLoss;
                double rsi = _avgLoss == 0 ? 100.0 : 100.0 - (100.0 / (1.0 + rs));
                _rsiValues.Add(rsi);
                _rsiDateTimes.Add(updateTime);              // <-- ADDED
                _previousClose = close;
            }
        }

        public double this[int index]
{
    get
    {
        if (index < 0 || index >= _rsiValues.Count) return double.NaN;
        return _rsiValues[_rsiValues.Count - 1 - index];
    }
}

        public DateTime GetRSIDateTime(int index)
{
    if (index < 0 || index >= _rsiDateTimes.Count) return DateTime.MinValue;
    return _rsiDateTimes[_rsiDateTimes.Count - 1 - index];
}

        public List<DateTime> GetRSIDateTimeList()
        {
            return new List<DateTime>(_rsiDateTimes);
        }

        public double CurrentRSI => _rsiValues.Count > 0 ? _rsiValues[_rsiValues.Count - 1] : double.NaN;
  
        public DateTime CurrentRSIDateTime => _rsiDateTimes.Count > 0 ? _rsiDateTimes[_rsiDateTimes.Count - 1] : DateTime.MinValue;

        public int Count => _rsiValues.Count;
    }
  
    public class OBV
    {
        private readonly List<double> _obvValues;
        private readonly List<DateTime> _obvDateTimes;
        private double _previousClose;
        private double _runningOBV;           // cumulative total
        private bool _isFirstBar;

        public OBV()
        {
            _obvValues = new List<double>();
            _obvDateTimes = new List<DateTime>();
            _previousClose = double.NaN;
            _runningOBV = 0.0;
            _isFirstBar = true;
        }

        // Full historical recalculation (used at strategy start)
        public void Calculate(TimeDataSeries timeDataSeries)
        {
            if (timeDataSeries == null || timeDataSeries.Count == 0) return;

            _obvValues.Clear();
            _obvDateTimes.Clear();
            _runningOBV = 0.0;
            _isFirstBar = true;
            _previousClose = double.NaN;

            foreach (var bar in timeDataSeries)
            {
                Update(bar);
            }
        }

        // Incremental update – called on every new bar (backtest + live)
        public void Update(IBarData newBar)
        {
            if (newBar == null) return;

            double close = newBar.Close;
            double volume = newBar.Volume;
            DateTime dt = newBar.DateTimeDT;

            if (_isFirstBar)
            {
                // First bar has no direction → OBV = 0 (or volume, convention varies)
                _runningOBV = 0.0;
                _isFirstBar = false;
            }
            else
            {
                if (close > _previousClose)
                    _runningOBV += volume;
                else if (close < _previousClose)
                    _runningOBV -= volume;
                // close == previousClose → no change
            }

            _obvValues.Add(_runningOBV);
            _obvDateTimes.Add(dt);
            _previousClose = close;
        }

        public void Update(double close, double volume, DateTime updateTime)
        {
            if (_isFirstBar)
            {
                _runningOBV = 0.0;
                _isFirstBar = false;
            }
            else
            {
                if (close > _previousClose)
                    _runningOBV += volume;
                else if (close < _previousClose)
                    _runningOBV -= volume;
            }

            _obvValues.Add(_runningOBV);
            _obvDateTimes.Add(updateTime);
            _previousClose = close;
        }

        // Indexer: [0] = current (most recent), [1] = previous, etc.
        public double this[int index]
        {
            get
            {
                if (index < 0 || index >= _obvValues.Count)
                    return double.NaN;
                return _obvValues[_obvValues.Count - 1 - index];
            }
        }

        public DateTime GetOBVDateTime(int index)
        {
            if (index < 0 || index >= _obvDateTimes.Count)
                return DateTime.MinValue;
            return _obvDateTimes[_obvDateTimes.Count - 1 - index];
        }


        /// <summary>
        /// Returns the lowest OBV value in the last 'lookback' bars (inclusive of current bar)
        /// </summary>
        public double Lowest(int lookback)
        {
            if (lookback <= 0 || _obvValues.Count == 0)
                return double.NaN;

            int startIdx = Math.Max(0, _obvValues.Count - lookback);
            double min = _obvValues[startIdx];

            for (int i = startIdx + 1; i < _obvValues.Count; i++)
            {
                if (_obvValues[i] < min)
                    min = _obvValues[i];
            }
            return min;
        }

        /// <summary>
        /// Returns the highest OBV value in the last 'lookback' bars (inclusive of current bar)
        /// </summary>
        public double Highest(int lookback)
        {
            if (lookback <= 0 || _obvValues.Count == 0)
                return double.NaN;

            int startIdx = Math.Max(0, _obvValues.Count - lookback);
            double max = _obvValues[startIdx];

            for (int i = startIdx + 1; i < _obvValues.Count; i++)
            {
                if (_obvValues[i] > max)
                    max = _obvValues[i];
            }
            return max;
        }


        public double CurrentOBV => _obvValues.Count > 0 ? _obvValues[_obvValues.Count - 1] : double.NaN;
        public double PreviousOBV => _obvValues.Count >= 2 ? _obvValues[_obvValues.Count - 2] : double.NaN;
        public int Count => _obvValues.Count;
    }
   
    public class ADX_Custom
    {
        private readonly int _period;
        private readonly double _wilderMultiplier;
        private readonly List<double> _adxValues;
        private readonly List<DateTime> _adxDateTimes;
        private double _prevClose;
        private double _prevPlusDM;
        private double _prevMinusDM;
        private double _prevTR;
        private double _smoothedPlusDM;
        private double _smoothedMinusDM;
        private double _smoothedTR;
        private readonly List<double> _dxValues;
        private int _barCount;
        private bool _isInitialized;
        private bool _enableLiveOverwrite = false;
        private DateTime _lastProcessedBarDate = DateTime.MinValue;

        // ────────────────────────────────────────────────
        // New properties for logging intermediates (read-only)
        // ────────────────────────────────────────────────
        public double High { get; private set; } = double.NaN;
        public double Low { get; private set; } = double.NaN;
        public double Close { get; private set; } = double.NaN;
        public double TR { get; private set; } = double.NaN;
        public double PlusDM { get; private set; } = double.NaN;
        public double MinusDM { get; private set; } = double.NaN;
        public double SmoothedTR => _smoothedTR;
        public double SmoothedPlusDM => _smoothedPlusDM;
        public double SmoothedMinusDM => _smoothedMinusDM;
        public double PlusDI => CurrentPlusDI;
        public double MinusDI => CurrentMinusDI;
        public double DX { get; private set; } = double.NaN;


        private StreamWriter _adxLog;

        private readonly bool _logging = true;

        public void SetLogger(StreamWriter log)
        {
            _adxLog = log;
            _adxLog.WriteLine("Mode,DateTime," +
                              "High,Low,Close," +
                              "TR,PlusDM,MinusDM," +
                              "SmTR,SmPlusDM,SmMinusDM," +
                              "PlusDI,MinusDI,DX,ADX", 
                              "BarCount");
        }

        private void LogAdxRow(string mode, DateTime dt)
        {
            if (!_logging) return;

            if (_adxLog == null)
                return;

            double adx = CurrentADX;
            double plusDI = CurrentPlusDI;
            double minusDI = CurrentMinusDI;

            _adxLog.WriteLine(
                $"{mode},{dt:yyyy-MM-dd HH:mm}," +
                $"{High},{Low},{Close},"+
                $"{TR},{PlusDM},{MinusDM},"+
                $"{_smoothedTR},{_smoothedPlusDM},{_smoothedMinusDM}," +
                $"{plusDI},{minusDI},{DX},{adx}," +
                $"{_barCount}"
            );
    
        }



        public ADX_Custom(int period = 14)
        {
            if (period <= 0)
                throw new ArgumentException("Period must be positive.");
            _period = period;
            _wilderMultiplier = 1.0 / period;
            _adxValues = new List<double>();
            _adxDateTimes = new List<DateTime>();
            _dxValues = new List<double>();
            _prevClose = double.NaN;
            _prevPlusDM = 0.0;
            _prevMinusDM = 0.0;
            _prevTR = 0.0;
            _smoothedPlusDM = 0.0;
            _smoothedMinusDM = 0.0;
            _smoothedTR = 0.0;
            _barCount = 0;
            _isInitialized = false;
        }

        public void CalculateFromHistory(TimeDataSeries timeDataSeries)
        {
            if (timeDataSeries == null || timeDataSeries.Count < _period + 1)
                return;
            _adxValues.Clear();
            _adxDateTimes.Clear();
            _dxValues.Clear();
            ResetInternalState();
            for (int i = 0; i < timeDataSeries.Count; i++)
            {
                IBarData bar = timeDataSeries[i];
                CalculateNew(bar);
                LogAdxRow("History", bar.DateTimeDT);  // 24.01
            }
        }

        public void CalculateNew(IBarData newBar)
        {
            if (newBar == null) return;

            double high = newBar.High;
            double low = newBar.Low;
            double close = newBar.Close;
            DateTime barTime = newBar.DateTimeDT;

            _barCount++;

            // First bar - no directional movement yet
            if (_barCount == 1)
            {
                _prevClose = close;
                _adxValues.Add(double.NaN);
                _adxDateTimes.Add(barTime);

                // Logging support
                High = high;
                Low = low;
                Close = close;
                TR = double.NaN;
                PlusDM = double.NaN;
                MinusDM = double.NaN;
                DX = double.NaN;

                return;
            }

            // Calculate True Range and Directional Movement
            double tr = Math.Max(high - low,
                       Math.Max(Math.Abs(high - _prevClose),
                                Math.Abs(low - _prevClose)));

            double plusDM = high - _prevClose > _prevClose - low ? Math.Max(high - _prevClose, 0.0) : 0.0;
            double minusDM = _prevClose - low > high - _prevClose ? Math.Max(_prevClose - low, 0.0) : 0.0;

            // Store for logging
            High = high;
            Low = low;
            Close = close;
            TR = tr;
            PlusDM = plusDM;
            MinusDM = minusDM;

            // Initial smoothing period
            if (_barCount <= _period)
            {
                _smoothedTR += tr;
                _smoothedPlusDM += plusDM;
                _smoothedMinusDM += minusDM;
                _adxValues.Add(double.NaN);
                _adxDateTimes.Add(barTime);
                DX = double.NaN;
            }

            if (_barCount == _period)
            {
                // First smoothed values (already added in loop above)
                double plusDI = 100.0 * _smoothedPlusDM / _smoothedTR;
                double minusDI = 100.0 * _smoothedMinusDM / _smoothedTR;
                double dx = Math.Abs(plusDI - minusDI) / (plusDI + minusDI) * 100.0;
                _dxValues.Add(dx);
                _adxValues.Add(double.NaN); // ADX starts one bar later
                _adxDateTimes.Add(barTime);
                DX = dx;
                _isInitialized = true;
            }
            else if (_barCount > _period)
            {
                // Wilder smoothing
                _smoothedTR = _smoothedTR - (_smoothedTR * _wilderMultiplier) + tr;
                _smoothedPlusDM = _smoothedPlusDM - (_smoothedPlusDM * _wilderMultiplier) + plusDM;
                _smoothedMinusDM = _smoothedMinusDM - (_smoothedMinusDM * _wilderMultiplier) + minusDM;

                double plusDI = _smoothedTR > 0 ? 100.0 * _smoothedPlusDM / _smoothedTR : 0.0;
                double minusDI = _smoothedTR > 0 ? 100.0 * _smoothedMinusDM / _smoothedTR : 0.0;
                double dx = (plusDI + minusDI) > 0 ? Math.Abs(plusDI - minusDI) / (plusDI + minusDI) * 100.0 : 0.0;

                _dxValues.Add(dx);
                DX = dx;

                // ADX is Wilder EMA of DX values
                if (_dxValues.Count == _period)
                {
                    double sumDx = _dxValues.Sum();
                    double adx = sumDx / _period;
                    _adxValues.Add(adx);
                }
                else if (_dxValues.Count > _period)
                {
                    double prevAdx = _adxValues[_adxValues.Count - 1];
                    double adx = (prevAdx * (_period - 1) + dx) / _period;
                    _adxValues.Add(adx);
                }
                else
                {
                    _adxValues.Add(double.NaN);
                }
                _adxDateTimes.Add(barTime);
            }

            _prevClose = close;
            LogAdxRow("BT", newBar.DateTimeDT);  // 24.01
        }
     
        private void ResetInternalState()
        {
            _prevClose = double.NaN;
            _smoothedPlusDM = 0.0;
            _smoothedMinusDM = 0.0;
            _smoothedTR = 0.0;
            _barCount = 0;
            _isInitialized = false;
        }

        public double this[int index] => index < 0 || index >= _adxValues.Count ? double.NaN : _adxValues[_adxValues.Count - 1 - index];

        public double CurrentADX => _adxValues.Count >= 1 ? _adxValues[_adxValues.Count - 1] : double.NaN;
        public double PreviousADX => _adxValues.Count >= 2 ? _adxValues[_adxValues.Count - 2] : double.NaN;
        public double CurrentPlusDI => _smoothedTR <= 0 ? double.NaN : 100.0 * Math.Max(_smoothedPlusDM, 0.0) / _smoothedTR;
        public double CurrentMinusDI => _smoothedTR <= 0 ? double.NaN : 100.0 * Math.Max(_smoothedMinusDM, 0.0) / _smoothedTR;
        public DateTime CurrentADXDateTime => _adxDateTimes.Count > 0 ? _adxDateTimes[_adxDateTimes.Count - 1] : DateTime.MinValue;
        public int Count => _adxValues.Count;

        public DateTime GetADXDateTime(int index)
        {
            if (index < 0 || index >= _adxDateTimes.Count)
                return DateTime.MinValue;
            return _adxDateTimes[_adxDateTimes.Count - 1 - index];
        }

        public double Highest(int lookback)
        {
            if (lookback <= 0 || _adxValues.Count == 0)
                return double.NaN;
            int startIdx = Math.Max(0, _adxValues.Count - lookback);
            double max = _adxValues[startIdx];
            for (int i = startIdx + 1; i < _adxValues.Count; i++)
            {
                if (_adxValues[i] > max)
                    max = _adxValues[i];
            }
            return max;
        }

        public double Lowest(int lookback)
        {
            if (lookback <= 0 || _adxValues.Count == 0)
                return double.NaN;
            int startIdx = Math.Max(0, _adxValues.Count - lookback);
            double min = _adxValues[startIdx];
            for (int i = startIdx + 1; i < _adxValues.Count; i++)
            {
                if (_adxValues[i] < min)
                    min = _adxValues[i];
            }
            return min;
        }
    }

    public class ADX_Daily
    {
       
        public readonly int _period;

        // ───────── Permanent (committed daily) state ─────────
        public double _permSmoothedTR;
        public double _permSmoothedPlusDM;
        public double _permSmoothedMinusDM;
        public double _permPrevClose;
        public double _permPrevHigh;
        public double _permPrevLow;

        public readonly List<double> _adxValues = new List<double>();
        public readonly List<DateTime> _adxDateTimes = new List<DateTime>();

        // ───────── Temporary (latest estimate) ─────────
        public double _tempSmoothedTR;
        public double _tempSmoothedPlusDM;
        public double _tempSmoothedMinusDM;
        public double _tempPrevClose;
        public double _tempPrevHigh;
        public double _tempPrevLow;

        // ───────── Debug / logging ─────────
        public double High { get; private set; }
        public double Low { get; private set; }
        public double Close { get; private set; }
        public double TR { get; private set; }
        public double PlusDM { get; private set; }
        public double MinusDM { get; private set; }
        public double DX { get; private set; }
        public double CurrentTempADX { get; private set; } = double.NaN;

        private int _dailyBarCount = 0;
        private double _dxSum = 0.0;
        private int _dxCount = 0;

        private readonly bool _logging = true;

        private StreamWriter _adxLog;

        public ADX_Daily(int period = 14)
        {
            if (period <= 0)
                throw new ArgumentException("Period must be positive.");
            _period = period;

            _permPrevClose = double.NaN;
            _permPrevHigh = double.NaN;
            _permPrevLow = double.NaN;

            
        }

        public void SetLogger(StreamWriter log)
        {
            _adxLog = log;
            _adxLog.WriteLine("Mode,DateTime," +
                              "High,Low,Close," +
                              "TR,PlusDM,MinusDM," +
                              "SmTR,SmPlusDM,SmMinusDM," +
                              "PlusDI,MinusDI,DX,ADX",
                              "BarCount");
        }

        private void LogAdxRow(string mode, DateTime dt, bool showCommitted)
        {
            if (!_logging) return;

            if (_adxLog == null) return;

            double smTR, smPlusDM, smMinusDM, plusDI, minusDI, adx;

            if (showCommitted)
            {
                smTR = _permSmoothedTR;
                smPlusDM = _permSmoothedPlusDM;
                smMinusDM = _permSmoothedMinusDM;
                plusDI = LastCommittedPlusDI;
                minusDI = LastCommittedMinusDI;
                adx = LastCommittedAdx;
            }
            else
            {
                smTR = _tempSmoothedTR;
                smPlusDM = _tempSmoothedPlusDM;
                smMinusDM = _tempSmoothedMinusDM;
                plusDI = CurrentTempPlusDI;
                minusDI = CurrentTempMinusDI;
                adx = CurrentTempADX;
            }

            _adxLog.WriteLine(
                $"{mode},{dt:yyyy-MM-dd HH:mm:ss}," +
                $"{High},{Low},{Close}," +                        
                $"{TR},{PlusDM},{MinusDM}," +                   
                $"{smTR},{smPlusDM},{smMinusDM}," +
                $"{plusDI},{minusDI},{DX},{adx}," +
                $"{_dailyBarCount}"
            );
        }

        public void CalculateFromHistory(TimeDataSeries timeDataSeries)
        {
            if (timeDataSeries == null || timeDataSeries.Count < 2)
                return;

            _adxValues.Clear();
            _adxDateTimes.Clear();
            _permSmoothedTR = 0;
            _permSmoothedPlusDM = 0;
            _permSmoothedMinusDM = 0;
            _dailyBarCount = 0;
            _dxSum = 0.0;
            _dxCount = 0;
            _permPrevClose = double.NaN;
            _permPrevHigh = double.NaN;
            _permPrevLow = double.NaN;

            foreach (var bar in timeDataSeries)
            {
                // Use fallback path (no temp involved)
                if (double.IsNaN(_permPrevClose))
                {
                    _permPrevClose = bar.Close;
                    _permPrevHigh = bar.High;
                    _permPrevLow = bar.Low;
                    continue;
                }

                _dailyBarCount++;

                double tr = Math.Max(bar.High - bar.Low,
                                     Math.Max(Math.Abs(bar.High - _permPrevClose),
                                              Math.Abs(bar.Low - _permPrevClose)));

                double upMove = bar.High - _permPrevHigh;
                double downMove = _permPrevLow - bar.Low;

                double plusDM = (upMove > downMove && upMove > 0) ? upMove : 0.0;
                double minusDM = (downMove > upMove && downMove > 0) ? downMove : 0.0;


                High = bar.High;
                Low = bar.Low;
                Close = bar.Close;
                TR = tr;
                PlusDM = plusDM;
                MinusDM = minusDM;


                if (_dailyBarCount == 1)
                {
                    _permSmoothedTR = tr;
                    _permSmoothedPlusDM = plusDM;
                    _permSmoothedMinusDM = minusDM;
                }
                else
                {
                    _permSmoothedTR = _permSmoothedTR - (_permSmoothedTR / _period) + tr;
                    _permSmoothedPlusDM = _permSmoothedPlusDM - (_permSmoothedPlusDM / _period) + plusDM;
                    _permSmoothedMinusDM = _permSmoothedMinusDM - (_permSmoothedMinusDM / _period) + minusDM;
                }

                double plusDI = _permSmoothedTR > 0 ? 100.0 * _permSmoothedPlusDM / _permSmoothedTR : 0.0;
                double minusDI = _permSmoothedTR > 0 ? 100.0 * _permSmoothedMinusDM / _permSmoothedTR : 0.0;
                double diSum = plusDI + minusDI;
                double dx = diSum > 0 ? Math.Abs(plusDI - minusDI) / diSum * 100.0 : 0.0;

                if (_dxCount < _period)
                {
                    _dxSum += dx;
                    _dxCount++;
                    if (_dxCount == _period)
                    {
                        double firstAdx = _dxSum / _period;
                        _adxValues.Add(firstAdx);
                        _adxDateTimes.Add(bar.DateTimeDT.Date);
                    }
                }
                else
                {
                    double prevAdx = _adxValues[_adxValues.Count - 1];
                    double adx = (prevAdx * (_period - 1) + dx) / _period;
                    _adxValues.Add(adx);
                    _adxDateTimes.Add(bar.DateTimeDT.Date);
                }

                _permPrevClose = bar.Close;
                _permPrevHigh = bar.High;
                _permPrevLow = bar.Low;
            }
        }

        public void OverWriteNew(IBarData developingBar)
        {

            // First time ever → initialize
            if (double.IsNaN(_permPrevClose))
            {
                _permPrevClose = developingBar.Close;
                _permPrevHigh = developingBar.High;
                _permPrevLow = developingBar.Low;

                _tempPrevClose = developingBar.Close;
                _tempPrevHigh = developingBar.High;
                _tempPrevLow = developingBar.Low;

                UpdateLoggingFields(developingBar);
                CurrentTempADX = double.NaN;
                return;
            }

            // Always reset temp from last committed values
            _tempSmoothedTR = _permSmoothedTR;
            _tempSmoothedPlusDM = _permSmoothedPlusDM;
            _tempSmoothedMinusDM = _permSmoothedMinusDM;
            _tempPrevClose = _permPrevClose;
            _tempPrevHigh = _permPrevHigh;
            _tempPrevLow = _permPrevLow;

            //Console.WriteLine($"Update base: permPrevClose={_permPrevClose:F2}, permHigh={_permPrevHigh:F2}, permLow={_permPrevLow:F2}");

            double tr = Math.Max(
                developingBar.High - developingBar.Low,
                Math.Max(Math.Abs(developingBar.High - _tempPrevClose), Math.Abs(developingBar.Low - _tempPrevClose)));

            double upMove = developingBar.High - _tempPrevHigh;
            double downMove = _tempPrevLow - developingBar.Low;

            double plusDM = (upMove > downMove && upMove > 0) ? upMove : 0.0;
            double minusDM = (downMove > upMove && downMove > 0) ? downMove : 0.0;

            High = developingBar.High;
            Low = developingBar.Low;
            Close = developingBar.Close;
            TR = tr;
            PlusDM = plusDM;
            MinusDM = minusDM;

            // Wilder smoothing (single step)
            _tempSmoothedTR = _tempSmoothedTR - (_tempSmoothedTR / _period) + tr;
            _tempSmoothedPlusDM = _tempSmoothedPlusDM - (_tempSmoothedPlusDM / _period) + plusDM;
            _tempSmoothedMinusDM = _tempSmoothedMinusDM - (_tempSmoothedMinusDM / _period) + minusDM;

            double plusDI = _tempSmoothedTR > 0 ? 100.0 * _tempSmoothedPlusDM / _tempSmoothedTR : 0.0;
            double minusDI = _tempSmoothedTR > 0 ? 100.0 * _tempSmoothedMinusDM / _tempSmoothedTR : 0.0;
            double dx = (plusDI + minusDI) > 0 ? Math.Abs(plusDI - minusDI) / (plusDI + minusDI) * 100.0 : 0.0;

            double prevAdx = _adxValues.Count > 0 ? _adxValues[_adxValues.Count - 1] : 0.0;
            CurrentTempADX = (prevAdx * (_period - 1) + dx) / _period;
            DX = dx;

            

            _tempPrevClose = developingBar.Close;
            _tempPrevHigh = developingBar.High;
            _tempPrevLow = developingBar.Low;

            LogAdxRow("OverWriteNew", developingBar.DateTimeDT, showCommitted: false);
        }

        private void UpdateLoggingFields(IBarData developingBar)
        {
           
            // LastTR, LastPlusDM, LastMinusDM, LastDX are set directly in Update
        }

        public void Commit(DateTime commitDate)
        {
            // Just copy temp → permanent (no recalculation)
            _permSmoothedTR = _tempSmoothedTR;
            _permSmoothedPlusDM = _tempSmoothedPlusDM;
            _permSmoothedMinusDM = _tempSmoothedMinusDM;
            _permPrevClose = _tempPrevClose;
            _permPrevHigh = _tempPrevHigh;
            _permPrevLow = _tempPrevLow;

            // Final DX + ADX from committed values
            double plusDI = _permSmoothedTR > 0 ? 100.0 * _permSmoothedPlusDM / _permSmoothedTR : 0.0;
            double minusDI = _permSmoothedTR > 0 ? 100.0 * _permSmoothedMinusDM / _permSmoothedTR : 0.0;
            double diSum = plusDI + minusDI;
            double dx = diSum > 0 ? Math.Abs(plusDI - minusDI) / diSum * 100.0 : 0.0;

            if (_dxCount < _period)
            {
                _dxSum += dx;
                _dxCount++;
                if (_dxCount == _period)
                {
                    double firstAdx = _dxSum / _period;
                    _adxValues.Add(firstAdx);
                    _adxDateTimes.Add(commitDate);
                }
            }
            else
            {
                double prevAdx = _adxValues[_adxValues.Count - 1];
                double adx = (prevAdx * (_period - 1) + dx) / _period;
                _adxValues.Add(adx);
                _adxDateTimes.Add(commitDate);
            }

            _dailyBarCount++;
            LogAdxRow("Commit", commitDate, showCommitted: true);
        }

        public double this[int index]
        {
            get
            {
                int idx = _adxValues.Count - 1 - index;
                return idx < 0 || idx >= _adxValues.Count ? double.NaN : _adxValues[idx];
            }
        }

        public double CurrentTempPlusDI
        {
            get
            {
                return _tempSmoothedTR > 0
                    ? 100.0 * _tempSmoothedPlusDM / _tempSmoothedTR
                    : double.NaN;
            }
        }

        public double CurrentTempMinusDI
        {
            get
            {
                return _tempSmoothedTR > 0
                    ? 100.0 * _tempSmoothedMinusDM / _tempSmoothedTR
                    : double.NaN;
            }
        }

        public double LastCommittedPlusDI
        {
            get
            {
                return _permSmoothedTR > 0
                    ? 100.0 * _permSmoothedPlusDM / _permSmoothedTR
                    : double.NaN;
            }
        }

        public double LastCommittedMinusDI
        {
            get
            {
                return _permSmoothedTR > 0
                    ? 100.0 * _permSmoothedMinusDM / _permSmoothedTR
                    : double.NaN;
            }
        }

        public double LastCommittedAdx => _adxValues.Count > 0 ? _adxValues[_adxValues.Count - 1] : double.NaN;
        public double PrevToLastCommittedAdx => _adxValues.Count > 1 ? _adxValues[_adxValues.Count - 2] : double.NaN;
        public int Count => _adxValues.Count;
    }



}

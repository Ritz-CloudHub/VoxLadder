
using QX.FinLib.Common;
using QX.FinLib.Data;
using QX.FinLib.Instrument;
using QX.FinLib.TSOptions;
using QX.FinLibDB;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace OptionDataAccessorImpIAPI
{

    // extended functionality
    // supports instrument caching
    // supports india vix data
    // supports future data along with spot for dual underlying strategies

    public class OptionDataAccessorX1API : IOptionDataAccessor
    {

        public enum DataType
        {
            None,
            Spot,
            Options,
            Future,
            NiftyInstrumentList,
            BankNiftyInstrumentList,
            SensexInstrumentList
        }

        public enum Operator
        {
            equal,
            greaterthan,
            lessthan,
            between

        }

        public enum RangeType
        {
            Delta,
            Premium
        }

        private readonly string _dbInstrumentName = "instrument.db";

        private bool EnableInstrumentCache { get; set; }

        private static char[] NUMCHARARRAY = new char[10] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' };

        private static string _apiBaseURL;

        private static uint _instrumentID = 100u;

        private bool _isMonthlyExpiry = true;

        private int _underlineMinTimeCompression = 1;

        private ConcurrentDictionary<string, OptionsInstrument> _optopnsInstrumentCollection = new ConcurrentDictionary<string, OptionsInstrument>();

        public OptionDataAccessorX1API(string apiBaseURL, bool enableInstrumentCache = true, int underlineMinTimeCompression = 1)
        {
            _apiBaseURL = apiBaseURL;
            _underlineMinTimeCompression = underlineMinTimeCompression;
            string message = "";
            if (string.IsNullOrEmpty(apiBaseURL))
            {
                throw new Exception(message);
            }

            this.EnableInstrumentCache = enableInstrumentCache;
        }

        public ConcurrentDictionary<string, OptionsInstrument> GetOptionInstrumentCollection(string symbolName, DateTime startDate, DateTime endDate, int lotSize, double minTickSize)
        {
            ConcurrentDictionary<string, OptionsInstrument> concurrentDictionary = new ConcurrentDictionary<string, OptionsInstrument>();
            string[] instrumentList = null;
            try
            {
                if (this.EnableInstrumentCache)
                {
                    try
                    {
                        instrumentList = GetAndCacheInstrumentList(symbolName);
                    }
                    catch (Exception ex)
                    {
                        if (instrumentList == null || instrumentList.Count() <= 0)
                        {
                            instrumentList = GetInstrumentList(symbolName);

                            if (instrumentList == null || instrumentList.Count() <= 0)
                            {
                                throw new Exception($"Error in fetching data:{ex.Message}");
                            }
                            else
                            {
                                StoreInstruments(instrumentList.ToList(), symbolName);

                            }
                        }
                    }

                }
                else
                {
                    instrumentList = GetInstrumentList(symbolName);
                }

            }
            catch (Exception ex)
            {
                throw new Exception(ex.Message);
            }

            string[] array = instrumentList;
            foreach (string text in array)
            {
                string text2 = text.Trim();
                OptionsInstrument value = (concurrentDictionary[text2] = InstrumentBase.CreateOptionsInstrumentFromName(ExchangeSegment.NSEFO, text2, lotSize, minTickSize));
                _optopnsInstrumentCollection[text2] = value;
                _instrumentID++;
            }

            return concurrentDictionary;
        }

        public OptionsInstrument GetNearestOptionsPremium(OptionType optionType, DateTime atDateTime, double premiumPrice, out double nearestPremium)
        {
            throw new Exception("Called GetNearestOptionPremium");

            nearestPremium = 0.0;
            try
            {
                //Debug.Assert(condition: false, "GetNearestOptionsPremium method is called");
                string query = string.Format("select InstrumentName, Close from tbl_options_intradayhistdata where TimeStamp = '{0}' and close >= {1} and OptionType = '{2}'order by close limit 1", atDateTime.ToString("yyyy-MM-dd HH:mm:ss"), premiumPrice, optionType.ToString().ToUpper());
                DataTable dBData = DBManager.Instance.GetDBData(query);
                if (dBData.Rows.Count == 0)
                {
                    return null;
                }

                OptionsInstrument value = null;
                string key = dBData.Rows[0].Field<string>("InstrumentName");
                nearestPremium = dBData.Rows[0].Field<double>("Close");
                if (_optopnsInstrumentCollection.TryGetValue(key, out value))
                {
                    return value;
                }

                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception in GetNearestOptionInstrument. {ex.Message}");
                throw new Exception($"Exception in GetNearestOptionInstrument. {ex.Message}");
            }

            return null;
        }

        public static bool ValidateInstrument(string instrumentName)
        {
            //string niftyPattern = @"^nifty\d{2}[a-zA-Z]{3}\d{2}\d+(CE|PE)$";
            //string bankNiftyPattern = @"^banknifty\d{2}[a-zA-Z]{3}\d{2}\d+(CE|PE)$";

            if (instrumentName.ToUpper().Contains("SENSEX"))
            {
                return true;
            }

            string niftyPattern = @"^nifty\d{2}[a-zA-Z]{3}[1-9]\d[1-9]\d{4}(CE|PE)$";
            string bankNiftyPattern = @"^banknifty\d{2}[a-zA-Z]{3}[1-9]\d[1-9]\d{4}(CE|PE)$";
            string sensexPattern = @"^sensex\d{2}[a-zA-Z]{3}[1-9]\d[1-9]\d{4}(CE|PE)$";

            return Regex.IsMatch(instrumentName, niftyPattern, RegexOptions.IgnoreCase) ||
                   Regex.IsMatch(instrumentName, bankNiftyPattern, RegexOptions.IgnoreCase) ||
                   Regex.IsMatch(instrumentName, sensexPattern, RegexOptions.IgnoreCase);
        }

        private string[] GetInstrumentList(string symbol)
        {
            try
            {
                string text = _apiBaseURL + "/getinstruments";
                string text2 = "";
                if (symbol.StartsWith("NIFTY", StringComparison.OrdinalIgnoreCase))
                {
                    text2 = "nifty";
                }
                else if (symbol.StartsWith("BANKNIFTY", StringComparison.OrdinalIgnoreCase))
                {
                    text2 = "banknifty";
                }
                else if (symbol.StartsWith("SENSEX", StringComparison.OrdinalIgnoreCase))
                {
                    text2 = "sensex";
                }

                string requestUri = text + "?symbol=" + text2;
                string text3 = "";
                HttpClient httpClient = new HttpClient();
                HttpResponseMessage result = httpClient.GetAsync(requestUri).Result;
                if (result.IsSuccessStatusCode)
                {
                    text3 = result.Content.ReadAsStringAsync().Result;
                    string[] array = text3.Split(new string[2] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                    //Console.WriteLine("List of instruments:");
                    string[] array2 = array;

                    string[] validInstruments = array2.Where(value => ValidateInstrument(value)).ToArray();

                    foreach (string value in validInstruments)
                    {
                        //Console.WriteLine(value);

                        if (!ValidateInstrument(value))
                        {
                            Debug.Assert(false);
                        }
                    }

                    return validInstruments;
                }

                throw new Exception($"Error: {result.StatusCode} - {result.ReasonPhrase}");
            }
            catch (Exception innerException)
            {
                throw new Exception("Exception in fetching data", innerException);
            }
        }
        private static readonly HttpClient _httpClient = new HttpClient();


        private string[] GetAndCacheInstrumentList(string symbol)
        {
            try
            {
                Stopwatch stopwatch = Stopwatch.StartNew();

                List<string> storedInstruments = GetInstrumentsFromDB(symbol).ToList();

                stopwatch.Stop();

                if (storedInstruments.Count > 0)
                {
                    Console.WriteLine($"Fetched {symbol} Instruments: Count {storedInstruments.Count}");
                    Console.WriteLine($"Execution Time to retrieve from Cache: {stopwatch.ElapsedMilliseconds} ms");
                }

                stopwatch.Start();

                string text = _apiBaseURL + "/getinstruments";
                string text2 = "";
                if (symbol.StartsWith("NIFTY", StringComparison.OrdinalIgnoreCase))
                {
                    text2 = "nifty";
                }
                else if (symbol.StartsWith("BANKNIFTY", StringComparison.OrdinalIgnoreCase))
                {
                    text2 = "banknifty";
                }
                else if (symbol.StartsWith("SENSEX", StringComparison.OrdinalIgnoreCase))
                {
                    text2 = "sensex";
                }

                string requestUri = text + "?symbol=" + text2;
                string text3 = "";
                HttpClient httpClient = new HttpClient();
                HttpResponseMessage result = httpClient.GetAsync(requestUri).Result;

                stopwatch.Stop();

                if (result.IsSuccessStatusCode)
                {
                    text3 = result.Content.ReadAsStringAsync().Result;
                    string[] array = text3.Split(new string[2] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                    string[] validInstruments = array.Where(value => ValidateInstrument(value)).ToArray();

                    List<string> newInstruments = validInstruments.Except(storedInstruments).ToList();

                    storedInstruments.AddRange(newInstruments);

                    Console.WriteLine($"Total {symbol} Instruments Count: {storedInstruments.Count} : New Instruments Count:{newInstruments.Count}");

                    _ = Task.Run(() => StoreInstruments(newInstruments, symbol));

                    return storedInstruments.ToArray();
                }

                Console.WriteLine($"Execution Time to retrieve from API: {stopwatch.ElapsedMilliseconds} ms");

                return storedInstruments.ToArray();
            }
            catch (Exception innerException)
            {
                throw new Exception("Exception in fetching data", innerException);
            }
        }
        private async Task StoreInstruments(List<string> instruments, string symbol)
        {
            try
            {
                string connectionString = $"Data Source={_dbInstrumentName}";

                using (var connection = new SQLiteConnection(connectionString))
                {
                    connection.Open();

                    // Create the table with a single column "Name"
                    string createTableQuery = @"
                    CREATE TABLE IF NOT EXISTS Instruments (
                        Name TEXT UNIQUE
                    );";
                    using (var command = new SQLiteCommand(createTableQuery, connection))
                    {
                        command.ExecuteNonQuery();
                    }

                    // Begin a transaction for batch insertions
                    using (var transaction = connection.BeginTransaction())
                    {
                        foreach (var instrument in instruments)
                        {
                            // Check if the instrument already exists
                            string checkQuery = "SELECT COUNT(*) FROM Instruments WHERE Name = @name;";
                            using (var checkCommand = new SQLiteCommand(checkQuery, connection, transaction))
                            {
                                checkCommand.Parameters.AddWithValue("@name", instrument);
                                long count = (long)checkCommand.ExecuteScalar();

                                if (count == 0) // Insert only if not exists
                                {
                                    string insertQuery = "INSERT INTO Instruments (Name) VALUES (@name);";
                                    using (var insertCommand = new SQLiteCommand(insertQuery, connection, transaction))
                                    {
                                        insertCommand.Parameters.AddWithValue("@name", instrument);
                                        insertCommand.ExecuteNonQuery();
                                    }
                                }
                            }
                        }

                        // Commit the transaction
                        transaction.Commit();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error storing instruments in SQLite: {ex.Message}");
                throw new Exception($"Error storing instruments in SQLite: {ex.Message}");
            }
        }

        private string[] GetInstrumentsFromDB(string symbol)
        {
            List<string> instruments = new List<string>();
            try
            {
                string connnectString = $"Data Source={_dbInstrumentName}";

                using (var connection = new SQLiteConnection(connnectString))
                {
                    connection.Open();

                    // Use LIKE with a wildcard (%) added to the symbol parameter
                    string selectQuery = "SELECT Name FROM Instruments WHERE Name LIKE @symbol;";

                    using (var command = new SQLiteCommand(selectQuery, connection))
                    {
                        // Add the '%' wildcard to the symbol parameter
                        command.Parameters.AddWithValue("@symbol", symbol + "%");

                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                instruments.Add(reader.GetString(0));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error retrieving instruments from SQLite: " + ex.Message);
                throw new Exception("Error retrieving instruments from SQLite: " + ex.Message);
            }
            return instruments.ToArray();
        }

        private static DataTable FetchDataFromAPI(string apiUrlWithParams, bool isSpotData = false)
        {
            try
            {
                string text = "";
                DataTable dataTable = new DataTable();
                using (HttpClient httpClient = new HttpClient())
                {
                    HttpResponseMessage result = httpClient.GetAsync(apiUrlWithParams).Result;
                    if (!result.IsSuccessStatusCode)
                    {
                        throw new Exception($"Error: {result.StatusCode} - {result.ReasonPhrase}");
                    }

                    text = result.Content.ReadAsStringAsync().Result;
                }

                string[] array = text.Split(new string[2] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                if (!isSpotData)
                {
                    dataTable.Columns.Add("InstrumentName", typeof(string));
                    dataTable.Columns.Add("Expiry", typeof(DateTime));
                    dataTable.Columns.Add("TimeStamp", typeof(DateTime));
                    dataTable.Columns.Add("Open", typeof(double));
                    dataTable.Columns.Add("High", typeof(double));
                    dataTable.Columns.Add("Low", typeof(double));
                    dataTable.Columns.Add("Close", typeof(double));
                    dataTable.Columns.Add("Volume", typeof(long));
                    dataTable.Columns.Add("OI", typeof(double));
                }
                else
                {
                    dataTable.Columns.Add("InstrumentName", typeof(string));
                    dataTable.Columns.Add("TimeStamp", typeof(DateTime));
                    dataTable.Columns.Add("Open", typeof(double));
                    dataTable.Columns.Add("High", typeof(double));
                    dataTable.Columns.Add("Low", typeof(double));
                    dataTable.Columns.Add("Close", typeof(double));
                }

                for (int i = 0; i < array.Length; i++)
                {
                    array[i].Trim();
                    string[] array2 = array[i].Split(',');
                    DataRow dataRow = dataTable.NewRow();
                    if (isSpotData)
                    {
                        if (array2.Length != 6)
                        {
                            throw new Exception("EXCEPTION:Invalid Number of Columns");
                        }

                        if (string.IsNullOrEmpty(array2[0]))
                        {
                            throw new ArgumentException("Value cannot be null or empty.");
                        }

                        dataRow["InstrumentName"] = array2[0].Trim();
                        if (string.IsNullOrEmpty(array2[1]))
                        {
                            throw new ArgumentException("Value cannot be null or empty.");
                        }

                        dataRow["TimeStamp"] = array2[1].Trim();
                        if (string.IsNullOrEmpty(array2[2]))
                        {
                            throw new ArgumentException("Value cannot be null or empty.");
                        }

                        dataRow["Open"] = array2[2].Trim();
                        if (string.IsNullOrEmpty(array2[3]))
                        {
                            throw new ArgumentException("Value cannot be null or empty.");
                        }

                        dataRow["High"] = array2[3].Trim();
                        if (string.IsNullOrEmpty(array2[4]))
                        {
                            throw new ArgumentException("Value cannot be null or empty.");
                        }

                        dataRow["Low"] = array2[4].Trim();
                        if (string.IsNullOrEmpty(array2[5]))
                        {
                            throw new ArgumentException("Value cannot be null or empty.");
                        }

                        dataRow["Close"] = array2[5].Trim();
                    }
                    else
                    {
                        if (array2.Length != 9)
                        {
                            throw new Exception("EXCEPTION:Invalid Number of Columns");
                        }

                        if (string.IsNullOrEmpty(array2[0]))
                        {
                            throw new ArgumentException("Value cannot be null or empty.");
                        }

                        dataRow["InstrumentName"] = array2[0].Trim();
                        if (string.IsNullOrEmpty(array2[1]))
                        {
                            throw new ArgumentException("Value cannot be null or empty.");
                        }

                        dataRow["Expiry"] = array2[1].Trim();
                        if (string.IsNullOrEmpty(array2[2]))
                        {
                            throw new ArgumentException("Value cannot be null or empty.");
                        }

                        dataRow["TimeStamp"] = array2[2].Trim();
                        if (string.IsNullOrEmpty(array2[3]))
                        {
                            throw new ArgumentException("Value cannot be null or empty.");
                        }

                        dataRow["Open"] = array2[3].Trim();
                        if (string.IsNullOrEmpty(array2[4]))
                        {
                            throw new ArgumentException("Value cannot be null or empty.");
                        }

                        dataRow["High"] = array2[4].Trim();
                        if (string.IsNullOrEmpty(array2[5]))
                        {
                            throw new ArgumentException("Value cannot be null or empty.");
                        }

                        dataRow["Low"] = array2[5].Trim();
                        if (string.IsNullOrEmpty(array2[6]))
                        {
                            throw new ArgumentException("Value cannot be null or empty.");
                        }

                        dataRow["Close"] = array2[6].Trim();
                        if (string.IsNullOrEmpty(array2[7]))
                        {
                            throw new ArgumentException("Value cannot be null or empty.");
                        }

                        dataRow["Volume"] = array2[7].Trim();
                        if (string.IsNullOrEmpty(array2[8]))
                        {
                            throw new ArgumentException("Value cannot be null or empty.");
                        }

                        dataRow["OI"] = array2[8].Trim();
                    }

                    dataTable.Rows.Add(dataRow);
                }

                return dataTable;
            }
            catch (Exception innerException)
            {
                Console.WriteLine($"Exception in fetching data{innerException.Message}");
                throw new Exception($"Exception in fetching data{innerException.Message}", innerException);
            }
        }
        private DataTable GetDataFromAPI(string apiUrlWithParams, bool isSpotData = false)
        {
            try
            {
                string text = "";
                DataTable dataTable = new DataTable();
                using (HttpClient httpClient = new HttpClient())
                {
                    HttpResponseMessage result = httpClient.GetAsync(apiUrlWithParams).Result;
                    if (!result.IsSuccessStatusCode)
                    {
                        throw new Exception($"Error: {result.StatusCode} - {result.ReasonPhrase}");
                    }

                    text = result.Content.ReadAsStringAsync().Result;
                }

                string[] array = text.Split(new string[2] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                if (!isSpotData)
                {
                    dataTable.Columns.Add("InstrumentName", typeof(string));
                    dataTable.Columns.Add("Expiry", typeof(DateTime));
                    dataTable.Columns.Add("TimeStamp", typeof(DateTime));
                    dataTable.Columns.Add("Open", typeof(double));
                    dataTable.Columns.Add("High", typeof(double));
                    dataTable.Columns.Add("Low", typeof(double));
                    dataTable.Columns.Add("Close", typeof(double));
                    dataTable.Columns.Add("Volume", typeof(long));
                    dataTable.Columns.Add("OI", typeof(double));
                }
                else
                {
                    dataTable.Columns.Add("InstrumentName", typeof(string));
                    dataTable.Columns.Add("TimeStamp", typeof(DateTime));
                    dataTable.Columns.Add("Open", typeof(double));
                    dataTable.Columns.Add("High", typeof(double));
                    dataTable.Columns.Add("Low", typeof(double));
                    dataTable.Columns.Add("Close", typeof(double));
                }

                for (int i = 0; i < array.Length; i++)
                {
                    array[i].Trim();
                    string[] array2 = array[i].Split(',');
                    DataRow dataRow = dataTable.NewRow();
                    if (isSpotData)
                    {
                        if (array2.Length != 6)
                        {
                            throw new Exception("EXCEPTION:Invalid Number of Columns");
                        }

                        if (string.IsNullOrEmpty(array2[0]))
                        {
                            throw new ArgumentException("Value cannot be null or empty.");
                        }

                        dataRow["InstrumentName"] = array2[0].Trim();
                        if (string.IsNullOrEmpty(array2[1]))
                        {
                            throw new ArgumentException("Value cannot be null or empty.");
                        }

                        dataRow["TimeStamp"] = array2[1].Trim();
                        if (string.IsNullOrEmpty(array2[2]))
                        {
                            throw new ArgumentException("Value cannot be null or empty.");
                        }

                        dataRow["Open"] = array2[2].Trim();
                        if (string.IsNullOrEmpty(array2[3]))
                        {
                            throw new ArgumentException("Value cannot be null or empty.");
                        }

                        dataRow["High"] = array2[3].Trim();
                        if (string.IsNullOrEmpty(array2[4]))
                        {
                            throw new ArgumentException("Value cannot be null or empty.");
                        }

                        dataRow["Low"] = array2[4].Trim();
                        if (string.IsNullOrEmpty(array2[5]))
                        {
                            throw new ArgumentException("Value cannot be null or empty.");
                        }

                        dataRow["Close"] = array2[5].Trim();
                    }
                    else
                    {
                        if (array2.Length != 9)
                        {
                            throw new Exception("EXCEPTION:Invalid Number of Columns");
                        }

                        if (string.IsNullOrEmpty(array2[0]))
                        {
                            throw new ArgumentException("Value cannot be null or empty.");
                        }

                        dataRow["InstrumentName"] = array2[0].Trim();
                        if (string.IsNullOrEmpty(array2[1]))
                        {
                            throw new ArgumentException("Value cannot be null or empty.");
                        }

                        dataRow["Expiry"] = array2[1].Trim();
                        if (string.IsNullOrEmpty(array2[2]))
                        {
                            throw new ArgumentException("Value cannot be null or empty.");
                        }

                        dataRow["TimeStamp"] = array2[2].Trim();
                        if (string.IsNullOrEmpty(array2[3]))
                        {
                            throw new ArgumentException("Value cannot be null or empty.");
                        }

                        dataRow["Open"] = array2[3].Trim();
                        if (string.IsNullOrEmpty(array2[4]))
                        {
                            throw new ArgumentException("Value cannot be null or empty.");
                        }

                        dataRow["High"] = array2[4].Trim();
                        if (string.IsNullOrEmpty(array2[5]))
                        {
                            throw new ArgumentException("Value cannot be null or empty.");
                        }

                        dataRow["Low"] = array2[5].Trim();
                        if (string.IsNullOrEmpty(array2[6]))
                        {
                            throw new ArgumentException("Value cannot be null or empty.");
                        }

                        dataRow["Close"] = array2[6].Trim();
                        if (string.IsNullOrEmpty(array2[7]))
                        {
                            throw new ArgumentException("Value cannot be null or empty.");
                        }

                        dataRow["Volume"] = array2[7].Trim();
                        if (string.IsNullOrEmpty(array2[8]))
                        {
                            throw new ArgumentException("Value cannot be null or empty.");
                        }

                        dataRow["OI"] = array2[8].Trim();
                    }

                    dataTable.Rows.Add(dataRow);
                }

                return dataTable;
            }
            catch (Exception innerException)
            {
                Console.WriteLine($"Exception in fetching data{innerException.Message}");
                throw new Exception($"Exception in fetching data{innerException.Message}", innerException);
            }
        }

        private string BuildApiURL(string InstrumentName, DateTime startDate, DateTime endDate, string segment = "indfut") //SKV:TODO:SENSEX
        {
            if (InstrumentName.ToUpper().Contains("SENSEX"))
            {
                if (segment == "indfut")
                {
                    segment = "bsefut";
                }
                else if (segment == "indspot")
                {
                    segment = "bsespot";
                }
                else if (segment == "indops")
                {
                    segment = "bseops";
                }
                else if (segment.Contains("opslist"))
                {
                    segment = "bseopslist";
                }
            }

            string text = _apiBaseURL + "/getnsedatastream";
            string text2 = startDate.ToString("yyyy-MM-dd 00:00:00");
            string text3 = endDate.ToString("yyyy-MM-dd 23:59:59");
            return text + "?segment=" + segment + "&symbol=" + InstrumentName + "&start_time=" + text2 + "&end_time=" + text3;
        }

        private static string GetURL(string InstrumentName, DateTime startDate, DateTime endDate, string segment = "indfut") //SKV:TODO:SENSEX
        {
            if (InstrumentName.ToUpper().Contains("SENSEX"))
            {
                if (segment == "indfut")
                {
                    segment = "bsefut";
                }
                else if (segment == "indspot")
                {
                    segment = "bsespot";
                }
                else if (segment == "indops")
                {
                    segment = "bseops";
                }
                else if (segment.Contains("opslist"))
                {
                    segment = "bseopslist";
                }
            }

            string text = _apiBaseURL + "/getnsedatastream";
            string text2 = startDate.ToString("yyyy-MM-dd 00:00:00");
            string text3 = endDate.ToString("yyyy-MM-dd 23:59:59");
            return text + "?segment=" + segment + "&symbol=" + InstrumentName + "&start_time=" + text2 + "&end_time=" + text3;
        }
        public TimeDataSeries GetUnderlineTimeDataSeries(string instrumentName, DateTime startDate, DateTime endDate)
        {
            TimeDataSeries timeDataSeries = new TimeDataSeries(BarCompression.MinuteBar, 60 * _underlineMinTimeCompression, BarType.CandleStick, new TimeSpan(9, 15, 0), new TimeSpan(15, 30, 0));
            try
            {

                bool isSpot = !instrumentName.Contains("-");

                DataType dataType = DataType.None;

                if (isSpot)
                {
                    dataType = DataType.Spot;
                }
                else
                {
                    dataType = DataType.Future;
                }

                string apiUrlWithParams = BuildApiURL(instrumentName, startDate, endDate);
                DataTable data;

                if (isSpot)
                {
                    apiUrlWithParams = BuildApiURL(instrumentName, startDate, endDate, "indspot"); //SKV:TODO:SENSEX
                    data = GetDataFromAPI(apiUrlWithParams, true);
                }
                else
                {
                    data = GetDataFromAPI(apiUrlWithParams);
                }

                foreach (DataRow row in data.Rows)
                {
                    DateTime barDateTime = row.Field<DateTime>("TimeStamp");
                    double open = row.Field<double>("Open");
                    double high = row.Field<double>("High");
                    double low = row.Field<double>("Low");
                    double close = row.Field<double>("Close");
                    long num = 0;
                    uint oi = 0;

                    if (!isSpot)
                    {
                        num = row.Field<long>("Volume");
                    }


                    timeDataSeries.AddBarData(barDateTime, open, high, low, close, num);
                }


            }
            catch (Exception ex)
            {
                Console.WriteLine("Exception in GetUnderlineTimeDataSeries. {0}", ex.Message);
                throw new Exception($"Exception in GetOptionTimeDataSeries. {ex.Message}");
            }

            return timeDataSeries;
        }
        public static TimeDataSeries GetIndiaVIXTimeDataSeries(DateTime startDate, DateTime endDate, int compressionInMin)
        {
            TimeDataSeries timeDataSeries = new TimeDataSeries(BarCompression.MinuteBar, 60 * compressionInMin, BarType.CandleStick, new TimeSpan(9, 15, 0), new TimeSpan(15, 30, 0));
            try
            {
                string instrumentName = "INDIAVIX";

                string apiUrlWithParams;
                DataTable data;
                apiUrlWithParams = GetURL(instrumentName, startDate, endDate, "indspot");
                data = FetchDataFromAPI(apiUrlWithParams, true);

                foreach (DataRow row in data.Rows)
                {
                    DateTime barDateTime = row.Field<DateTime>("TimeStamp");
                    double open = row.Field<double>("Open");
                    double high = row.Field<double>("High");
                    double low = row.Field<double>("Low");
                    double close = row.Field<double>("Close");
                    long num = 0;
                    uint oi = 0;

                    timeDataSeries.AddBarData(barDateTime, open, high, low, close, num);
                }


            }
            catch (Exception ex)
            {
                Console.WriteLine("Exception in GetIndiaVIXTimeDataSeries. {0}", ex.Message);
                throw new Exception($"Exception in GetIndiaVIXTimeDataSeries. {ex.Message}");
            }

            return timeDataSeries;
        }
        public static TimeDataSeries GetFutureTimeDataSeries(string instrumentName, DateTime startDate, DateTime endDate, int compressionInMin)
        {
            TimeDataSeries timeDataSeries = new TimeDataSeries(BarCompression.MinuteBar, 60 * compressionInMin, BarType.CandleStick, new TimeSpan(9, 15, 0), new TimeSpan(15, 30, 0));


            try
            {
                DataType dataType = DataType.Future;

                string apiUrlWithParams = GetURL(instrumentName, startDate, endDate, "indfut");
                DataTable data;

                data = FetchDataFromAPI(apiUrlWithParams, false);

                foreach (DataRow row in data.Rows)
                {
                    DateTime barDateTime = row.Field<DateTime>("TimeStamp");
                    double open = row.Field<double>("Open");
                    double high = row.Field<double>("High");
                    double low = row.Field<double>("Low");
                    double close = row.Field<double>("Close");
                    long num = 0;
                    uint oi = 0;

                    num = row.Field<long>("Volume");

                    timeDataSeries.AddBarData(barDateTime, open, high, low, close, num);
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine("Exception in GetUnderlineTimeDataSeries. {0}", ex.Message);
                throw new Exception($"Exception in GetOptionTimeDataSeries. {ex.Message}");
            }

            return timeDataSeries;
        }


        private int CheckDateContinuity(DateTime prevTimeStamp, DateTime timeStamp)
        {
            if (prevTimeStamp == DateTime.MinValue)
            {
                return 1;
            }

            Debug.Assert(condition: true);
            TimeSpan timeSpan = timeStamp - prevTimeStamp;
            int num = (int)timeSpan.TotalMinutes;
            int num2 = (int)Math.Ceiling(timeSpan.TotalDays);
            if (num == 1)
            {
                return 1;
            }

            if (num2 == 1 && timeStamp.TimeOfDay == new TimeSpan(9, 15, 0) && prevTimeStamp.TimeOfDay == new TimeSpan(15, 29, 0))
            {
                return 1;
            }

            return num;
        }




        //public TimeDataSeries GetOptionTimeDataSeriesRange(RangeType rangeType, string symbolName, Operator ops, double value1, double value2, DateTime startDate, bool isWeekly = true)
        //{
        //    TimeDataSeries timeDataSeries;

        //    StartWith(symbolName)

        //    return timeDataSeries;
        //}

        public TimeDataSeries GetOptionTimeDataSeries(string instrumentName, DateTime startDate, bool isWeekly = true)
        {
            TimeDataSeries timeDataSeries = null;
            try
            {
                DateTime endDate = new DateTime(2030, 12, 31);
                string apiUrlWithParams = BuildApiURL(instrumentName, startDate, endDate, "indops"); //SKV:TODO: SENSEX
                //DataTable data = GetData(apiUrlWithParams);

                DataTable data = GetDataFromAPI(apiUrlWithParams);

                timeDataSeries = new TimeDataSeries(BarCompression.MinuteBar, 60, BarType.CandleStick, new TimeSpan(9, 15, 0), new TimeSpan(15, 30, 0));
                DateTime minValue = DateTime.MinValue;
                double num = 0.0;
                double num2 = 0.0;
                double num3 = 0.0;
                double num4 = 0.0;
                long num5 = 0L;
                foreach (DataRow row in data.Rows)
                {
                    DateTime dateTime = row.Field<DateTime>("TimeStamp");
                    double num6 = row.Field<double>("Open");
                    double num7 = row.Field<double>("High");
                    double num8 = row.Field<double>("Low");
                    double num9 = row.Field<double>("Close");
                    long num10 = row.Field<long>("Volume");
                    timeDataSeries.AddBarData(dateTime, num6, num7, num8, num9, num10);
                    minValue = dateTime;
                    num = num6;
                    num2 = num7;
                    num3 = num8;
                    num4 = num9;
                    num5 = num10;
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception in GetOptionTimeDataSeries. {ex.Message}");
                throw new Exception($"Exception in GetOptionTimeDataSeries. {ex.Message}");
            }

            return timeDataSeries;
        }


        public TimeDataSeries GetOptionTimeDataSeriesHK(string instrumentName, DateTime startDate, int compressionInMin, bool isWeekly = true)
        {
            TimeDataSeries timeDataSeries = null;
            try
            {
                DateTime endDate = new DateTime(2030, 12, 31);
                string apiUrlWithParams = BuildApiURL(instrumentName, startDate, endDate, "indops"); //SKV:TODO: SENSEX
                //DataTable data = GetData(apiUrlWithParams);

                DataTable data = GetDataFromAPI(apiUrlWithParams);

                timeDataSeries = new TimeDataSeries(BarCompression.MinuteBar, 60 * compressionInMin, BarType.HeikinAshi, new TimeSpan(9, 15, 0), new TimeSpan(15, 30, 0));
                DateTime minValue = DateTime.MinValue;
                double num = 0.0;
                double num2 = 0.0;
                double num3 = 0.0;
                double num4 = 0.0;
                long num5 = 0L;
                foreach (DataRow row in data.Rows)
                {
                    DateTime dateTime = row.Field<DateTime>("TimeStamp");
                    double num6 = row.Field<double>("Open");
                    double num7 = row.Field<double>("High");
                    double num8 = row.Field<double>("Low");
                    double num9 = row.Field<double>("Close");
                    long num10 = row.Field<long>("Volume");
                    timeDataSeries.AddBarData(dateTime, num6, num7, num8, num9, num10);
                    minValue = dateTime;
                    num = num6;
                    num2 = num7;
                    num3 = num8;
                    num4 = num9;
                    num5 = num10;
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception in GetOptionTimeDataSeries. {ex.Message}");
                throw new Exception($"Exception in GetOptionTimeDataSeries. {ex.Message}");
            }

            return timeDataSeries;
        }

    }
}
#if false // Decompilation log
'27' items in cache
------------------
Resolve: 'mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089'
Found single assembly: 'mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089'
Load from: 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\mscorlib.dll'
------------------
Resolve: 'QXFinLib, Version=1.0.1.1, Culture=neutral, PublicKeyToken=null'
Found single assembly: 'QXFinLib, Version=1.0.1.1, Culture=neutral, PublicKeyToken=null'
Load from: 'D:\OneDrive\Blitz Strategies\IronFly\IronFly\reference\QXFinLib.dll'
------------------
Resolve: 'System.Data, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089'
Found single assembly: 'System.Data, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089'
Load from: 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\System.Data.dll'
------------------
Resolve: 'System.Net.Http, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a'
Found single assembly: 'System.Net.Http, Version=4.2.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a'
WARN: Version mismatch. Expected: '4.0.0.0', Got: '4.2.0.0'
Load from: 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\System.Net.Http.dll'
------------------
Resolve: 'System, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089'
Found single assembly: 'System, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089'
Load from: 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\System.dll'
------------------
Resolve: 'QX.FinLibDB, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null'
Found single assembly: 'QX.FinLibDB, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null'
Load from: 'D:\OneDrive\Blitz Strategies\IronFly\IronFly\bin\Debug\QX.FinLibDB.dll'
------------------
Resolve: 'System.Data.DataSetExtensions, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089'
Found single assembly: 'System.Data.DataSetExtensions, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089'
Load from: 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\System.Data.DataSetExtensions.dll'
#endif

﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using CTP;
using log4net;
using log4net.Repository.Hierarchy;
using MathNet.Numerics;
using MathNet.Numerics.LinearAlgebra.Double;
using SendMail;

namespace WrapperTest
{
    public enum ChannelType
    {
        模拟24X7,
        模拟交易所,
        华泰期货,
        宏源期货
    }

    public enum 多空性质
    {
        多开,
        多平,
        空开,
        空平,
        双开双平多换空换
    }

    public static class MathUtils
    {
        /// <summary>
        /// 拟合的直线斜角要求，单位是角度
        /// </summary>
        public static double Slope = 45;

        /// <summary>
        /// 仅根据走势开仓的拟合的直线斜角要求，单位是角度
        /// </summary>
        public static double Slope2 = 50;

        public static List<double> GetMovingAverage(List<double> source, int interval = 3)
        {
            try
            {
                var result = new List<double>();

                //补interval - 1个数在最前面
                var temp = new List<double>();
                for (var i = 0; i < interval - 1; i++)
                {
                    temp.Add(source[0]);
                }
                temp.AddRange(source);

                for (var i = interval - 1; i < temp.Count; i++)
                {
                    double sum = 0;
                    for (var j = i; j >= i - interval + 1; j--)
                    {
                        sum += temp[j];
                    }

                    var average = sum/interval;
                    result.Add(average);
                }
                return result;
            }
            catch (Exception ex)
            {
                Utils.WriteException(ex);
            }
            return null;
        }

        /// <summary>
        /// 根据连续几个行情，判断最近的趋势是不是向上，排除太平的，要求大于tan15度
        /// </summary>
        /// <param name="xdata"></param>
        /// <param name="ydata"></param>
        /// <param name="slope"></param>
        /// <param name="ma"></param>
        /// <returns></returns>
        public static Tuple<bool, double, double> IsPointingUp(List<double> xdata, List<double> ydata, double slope)
        {
            try
            {
                //至少需要两个点
                if (xdata.Count < 2 || ydata.Count < 2)
                {
                    return new Tuple<bool, double, double>(false, 0, 0);
                }

                //万一遇到数量不等的时候，进行补救
                if (xdata.Count != ydata.Count)
                {
                    var diff = Math.Abs(xdata.Count - ydata.Count);

                    if (xdata.Count > ydata.Count)
                    {
                        var last = ydata[ydata.Count - 1];

                        for (var i = 0; i < diff; i++)
                        {
                            ydata.Add(last);
                        }
                    }
                    else
                    {
                        var last = xdata[xdata.Count - 1];

                        for (var i = 0; i < diff; i++)
                        {
                            xdata.Add(last);
                        }
                    }
                }

                var line = Fit.Line(xdata.ToArray(), ydata.ToArray());
                return new Tuple<bool, double, double>(line.Item2 > Math.Tan(slope/180.0*Math.PI), line.Item2,
                    Math.Atan(line.Item2)*180/Math.PI);
            }
            catch (Exception ex)
            {
                Utils.WriteException(ex);
            }

            return new Tuple<bool, double, double>(false, 0, 0);
        }

        /// <summary>
        /// 根据连续几个行情，判断最近的趋势是不是向下，排除太平的，要求大于tan15度
        /// </summary>
        /// <param name="xdata"></param>
        /// <param name="ydata"></param>
        /// <param name="slope"></param>
        /// <param name="ma"></param>
        /// <returns></returns>
        public static Tuple<bool, double, double> IsPointingDown(List<double> xdata, List<double> ydata, double slope)
        {
            try
            {
                //至少需要两个点
                if (xdata.Count < 2 || ydata.Count < 2)
                {
                    return new Tuple<bool, double, double>(false, 0, 0);
                }

                //万一遇到数量不等的时候，进行补救
                if (xdata.Count != ydata.Count)
                {
                    var diff = Math.Abs(xdata.Count - ydata.Count);

                    if (xdata.Count > ydata.Count)
                    {
                        var last = ydata[ydata.Count - 1];

                        for (var i = 0; i < diff; i++)
                        {
                            ydata.Add(last);
                        }
                    }
                    else
                    {
                        var last = xdata[xdata.Count - 1];

                        for (var i = 0; i < diff; i++)
                        {
                            xdata.Add(last);
                        }
                    }
                }

                var line = Fit.Line(xdata.ToArray(), ydata.ToArray());
                return new Tuple<bool, double, double>(line.Item2 < Math.Tan(-slope/180.0*Math.PI), line.Item2,
                    Math.Atan(line.Item2)*180/Math.PI);
            }
            catch (Exception ex)
            {
                Utils.WriteException(ex);
            }

            return new Tuple<bool, double, double>(false, 0, 0);
        }
    }

    public interface ITraderAdapter
    {
        void CloseAllPositions();
    }

    public static class Utils
    {
        public static object Locker = new object();
        public static object Locker2 = new object();
        public static object LockerQuote = new object();
        public static bool IsTraderReady = false;
        public static object Trader;
        public static object QuoteMain;
        public static ILog LogDebug;
        public static ILog LogInfo;
        public static ConcurrentDictionary<string, ILog> LogQuotes;
        public static ConcurrentDictionary<string, ILog> LogMinuteQuotes;
        public static ILog LogStopLossPrices;

        public static readonly string AssemblyPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) +
                                                     "\\";

        public static bool IsMailingEnabled;
        public static bool IsInitialized = false;
        public static double GoUpRangeComparedToPreClosePrice = 1.01;
        public static double FallDownRangeComparedToPreClosePrice = 0.99;
        public static double GoUpRangeComparedToLowestPrice = 1.005;
        public static double LargeNumber = 999999;
        /// <summary>
        /// 开仓时沿均线的误差值
        /// </summary>
        public static double OpenTolerance = 0.0003;

        /// <summary>
        /// 止损平仓时，最新价偏离成本价的幅度限制
        /// </summary>
        public static double CloseTolerance = 0.01;

        /// <summary>
        /// 当前距离比最高距离的比值限制，低于该值时止损
        /// </summary>
        public static double CurrentDistanceToHighestDistanceRatioLimit = 0.7;

        /// <summary>
        /// 当最高距离为最新价的某个幅度时，才考虑这种止损，避免多次小止损
        /// </summary>
        public static double HighestDistanceConsiderLimit = 0.004;

        public static double InstrumentTotalPrice = 40000;

        public static double FallDonwRangeComparedToHighestPrice = 0.995;

        public static double StopLossUpperRange = 1.005;
        public static double StopLossLowerRange = 0.995;
        public static double LimitCloseRange = 0.995;
        public static int OpenVolumePerTime = 1;
        public static int CategoryUpperLimit = 8;

        /// <summary>
        /// 用于拟合的分时图节点个数之一，短
        /// </summary>
        public static int MinuteByMinuteSizeShort = 5;

        /// <summary>
        /// 用于拟合的分时图节点个数之一，长
        /// </summary>
        public static int MinuteByMinuteSizeLong = 20;

        /// <summary>
        /// 用于拟合的分时图节点个数之一，中
        /// </summary>
        public static int MinuteByMinuteSizeMiddle = 10;

        /// <summary>
        /// simnow账号
        /// </summary>
        public static string SimNowAccount;

        /// <summary>
        /// simnow密码
        /// </summary>
        public static string SimNowPassword;

        /// <summary>
        /// 仅根据走势开仓时，偏离均线的幅度限制，如果超过了，认为已经错过了开仓时机
        /// </summary>
        public static double OpenAccordingToTrendLimit = 0.01;

        /// <summary>
        /// 振幅要求
        /// </summary>
        public static double SwingLimit = 0.005;

        /// <summary>
        /// 止盈比例
        /// </summary>
        public static double StopProfitRatio = 0.02;

        public static List<string> AllowedCategories = new List<string>();
        public static ChannelType CurrentChannel = ChannelType.模拟交易所;
        public static int ExchangeTimeOffset = 0;
        public static List<double> MinuteShortXData;
        public static List<double> MinuteLongXData;
        public static List<double> MinuteMiddleXData;
        public static bool IsOpenLocked = false;

        public static void GetQuoteLoggers()
        {
            LogQuotes = new ConcurrentDictionary<string, ILog>();
            LogMinuteQuotes = new ConcurrentDictionary<string, ILog>();

            foreach (var category in AllowedCategories)
            {
                var log = LogManager.GetLogger(category);
                LogQuotes[category] = log;

                var logMinute = LogManager.GetLogger(category + "Minute");
                LogMinuteQuotes[category] = logMinute;
            }

            MinuteShortXData = new List<double>();

            for (var i = 0; i < MinuteByMinuteSizeShort; i++)
            {
                MinuteShortXData.Add(i);
            }

            MinuteLongXData = new List<double>();

            for (var i = 0; i < MinuteByMinuteSizeLong; i++)
            {
                MinuteLongXData.Add(i);
            }

            MinuteMiddleXData = new List<double>();

            for (var i = 0; i < MinuteByMinuteSizeMiddle; i++)
            {
                MinuteMiddleXData.Add(i);
            }
        }

        //public static void GetStopLossPricesLogger()
        //{
        //    LogStopLossPrices = LogManager.GetLogger(string.Format("{0}StopLossPrices", CurrentChannel));
        //}

        public static void GetDebugAndInfoLoggers()
        {
            LogDebug = LogManager.GetLogger("logDebug");
            LogInfo = LogManager.GetLogger("logInfo");
        }

        public static string GetInstrumentCategory(string instrumentId)
        {
            var regex = new Regex("^[a-zA-Z]+");
            var match = regex.Match(instrumentId);

            if (match.Success)
            {
                Debug.Assert(match.Value.Length <= 2);
                return match.Value;
            }

            return null;
        }

        public static string GetHourAndMinute(string time)
        {
            //21:09:00
            return time.Substring(0, 5);
        }

        public static string FormatQuote(ThostFtdcDepthMarketDataField pDepthMarketData, int xianshou = 0, double cangcha = 0.0, 多空性质 xingzhi = 多空性质.双开双平多换空换)
        {
            var s =
                string.Format(
                    "合约:{0},最新:{1},开盘:{2},昨结:{3},昨收:{4},最高:{5},最低:{6},涨停:{7},跌停:{8},更新时间:{9},毫秒:{10},平均:{11:N2},最新距平均:{12:N2},成交量:{13},交易日:{14},持仓:{15},买一价:{16},买一量:{17},卖一价:{18},卖一量:{19},现手:{20},仓差:{21},性质:{22}",
                    pDepthMarketData.InstrumentID, pDepthMarketData.LastPrice, pDepthMarketData.OpenPrice,
                    pDepthMarketData.PreSettlementPrice,
                    pDepthMarketData.PreClosePrice, pDepthMarketData.HighestPrice, pDepthMarketData.LowestPrice,
                    pDepthMarketData.UpperLimitPrice, pDepthMarketData.LowerLimitPrice, pDepthMarketData.UpdateTime,
                    pDepthMarketData.UpdateMillisec,
                    GetAveragePrice(pDepthMarketData), pDepthMarketData.LastPrice - GetAveragePrice(pDepthMarketData),
                    pDepthMarketData.Volume, pDepthMarketData.TradingDay, pDepthMarketData.OpenInterest, pDepthMarketData.BidPrice1, pDepthMarketData.BidVolume1, pDepthMarketData.AskPrice1, pDepthMarketData.AskVolume1, xianshou, cangcha, xingzhi);

            return s;
        }

        public static void WriteQuote(ThostFtdcDepthMarketDataField pDepthMarketData)
        {
            try
            {
                if (pDepthMarketData == null)
                {
                    WriteLine("排除空行情", true);
                    return;
                }


                var instrumentId = pDepthMarketData.InstrumentID;

                //保存从程序启动以来的最高价、最低价
                if (InstrumentToMaxAndMinPrice.ContainsKey(instrumentId))
                {
                    var mamp = InstrumentToMaxAndMinPrice[instrumentId];
                    mamp.MinPrice = Math.Min(mamp.MinPrice, pDepthMarketData.LastPrice);
                    mamp.MaxPrice = Math.Max(mamp.MaxPrice, pDepthMarketData.LastPrice);
                    WriteLine(string.Format("当前最高价{0},最低价{1},昨结价{2},振幅{3:P}", mamp.MaxPrice, mamp.MinPrice,
                        pDepthMarketData.PreSettlementPrice, mamp.Swing/pDepthMarketData.PreSettlementPrice));
                }
                else
                {
                    var mamp = new MaxAndMinPrice();
                    mamp.MaxPrice = mamp.MinPrice = pDepthMarketData.LastPrice;
                    InstrumentToMaxAndMinPrice[instrumentId] = mamp;
                }

                //保存前几个行情
                if (InstrumentToQuotes.ContainsKey(instrumentId))
                {
                    var preQuotes = InstrumentToQuotes[instrumentId];

                    if (preQuotes.Count > 0)
                    {
                        var preQuote = preQuotes[preQuotes.Count - 1];

                        if (CurrentChannel != ChannelType.模拟24X7 && preQuote != null &&
                            preQuote.Volume >= pDepthMarketData.Volume) //刚来的行情比以前的行情还要旧，抛弃掉
                        {
                            WriteLine(
                                string.Format("{0}的新行情成交量{1}小于等于先前行情的成交量{2}，抛弃", pDepthMarketData.InstrumentID,
                                    pDepthMarketData.Volume, preQuote.Volume));
                            return;
                        }

                        if (!InstrumentToMinuteByMinuteChart.ContainsKey(instrumentId))
                        {
                            InstrumentToMinuteByMinuteChart[instrumentId] = new List<Tuple<string, Quote>>();
                        }

                        var minuteByMinuteChart = InstrumentToMinuteByMinuteChart[instrumentId];

                        var hourAndMinute = GetHourAndMinute(pDepthMarketData.UpdateTime);
                        var tuple = new Tuple<string, Quote>(hourAndMinute,
                            new Quote
                            {
                                MarketData = pDepthMarketData,
                                LastPrice = pDepthMarketData.LastPrice,
                                AveragePrice = pDepthMarketData.AveragePrice
                            });

                        if (minuteByMinuteChart.Count > 0) //最后一个行情是最新一分钟的
                        {
                            var lastMinuteQuote = minuteByMinuteChart[minuteByMinuteChart.Count - 1];

                            if (lastMinuteQuote.Item1.Equals(tuple.Item1)) //相同分钟，替换
                            {
                                lastMinuteQuote = tuple;
                            }
                            else //新加入的行情是下一分钟的，添加
                            {
                                minuteByMinuteChart.Add(tuple);
                            }
                        }
                        else //行情列表是空的，直接添加
                        {
                            minuteByMinuteChart.Add(tuple);
                        }
                       
                        //到下一分钟之前，记录当前分钟的最后一个行情，作为分时图节点
                        if (preQuote != null &&
                            GetHourAndMinute(preQuote.UpdateTime) != GetHourAndMinute(pDepthMarketData.UpdateTime))
                        {
                            try
                            {
                                LogMinuteQuotes[GetInstrumentCategory(pDepthMarketData.InstrumentID)].Debug(
                                    FormatQuote(preQuote));
                            }
                            catch (Exception)
                            {

                            }
                        }
                    }

                }

                //保存当前行情
                if (!InstrumentToQuotes.ContainsKey(pDepthMarketData.InstrumentID))
                {
                    InstrumentToQuotes[pDepthMarketData.InstrumentID] = new List<ThostFtdcDepthMarketDataField>();
                }

                var xianshou = 0;
                var cangcha = 0.0;
                var xingzhi = 多空性质.双开双平多换空换;

                //计算多开、多平、空开、空平
                if (InstrumentToLastTick.ContainsKey(instrumentId)) //至少要两个tick行情才能计算
                {
                    var preTick = InstrumentToLastTick[instrumentId];

                    xianshou = pDepthMarketData.Volume - preTick.Volume;
                    cangcha = pDepthMarketData.OpenInterest - preTick.OpenInterest;
                    xingzhi = 多空性质.双开双平多换空换;

                    if (xianshou != Math.Abs(cangcha)) //不考虑双开\双平\多换\空换情况
                    {
                        if (cangcha != 0)
                        {
                            if (cangcha > 0) //开仓
                            {
                                if (pDepthMarketData.LastPrice == preTick.AskPrice1) //以卖价成交，多开
                                {
                                    xingzhi = 多空性质.多开;
                                }

                                if (pDepthMarketData.LastPrice == preTick.BidPrice1) //以买价成交，空开
                                {
                                    xingzhi = 多空性质.空开;
                                }
                            }
                            else
                            {
                                if (cangcha < 0) //平仓
                                {
                                    if (pDepthMarketData.LastPrice == preTick.AskPrice1) //以卖价成交，空平
                                    {
                                        xingzhi = 多空性质.空平;
                                    }

                                    if (pDepthMarketData.LastPrice == preTick.BidPrice1) //以买价成交，多平
                                    {
                                        xingzhi = 多空性质.多平;
                                    }
                                }
                            }
                        }
                    }
                }

                InstrumentToLastTick[instrumentId] = pDepthMarketData;
                InstrumentToQuotes[pDepthMarketData.InstrumentID].Add(pDepthMarketData);

                //每10个行情取均值，计算多空仓止损参考价
                var movingAverageCount = 10;

                if (InstrumentToQuotes.ContainsKey(pDepthMarketData.InstrumentID))
                {
                    var quotes = InstrumentToQuotes[pDepthMarketData.InstrumentID];

                    if (quotes.Count >= movingAverageCount)
                    {
                        var listTemp = new List<ThostFtdcDepthMarketDataField>();
                        var nullCount = 0;
                        for (var i = quotes.Count - 1; i >= quotes.Count - movingAverageCount; i--)
                        {
                            //行情里面会有空值
                            if (quotes[i] != null && quotes[i].LastPrice >= pDepthMarketData.LowerLimitPrice && quotes[i].LastPrice <= pDepthMarketData.UpperLimitPrice)
                            {
                                listTemp.Add(quotes[i]);
                            }
                            else
                            {
                                nullCount++;
                                WriteLine(string.Format("发现无效值,nullCount={0}", nullCount), true);
                            }
                        }

                        var averageLastPrice = listTemp.Average(p => p.LastPrice);

                        if (InstrumentToStopLossPrices.ContainsKey(pDepthMarketData.InstrumentID))
                        {
                            if (averageLastPrice >= pDepthMarketData.LowestPrice &&
                                averageLastPrice <= pDepthMarketData.HighestPrice)
                            {
                                var stopLossPrices = InstrumentToStopLossPrices[pDepthMarketData.InstrumentID];

                                if (stopLossPrices.ForLong < averageLastPrice) //随着最新价的走高，不断推高多仓的止损参考价
                                {
                                    stopLossPrices.ForLong = averageLastPrice;
                                    WriteLine(
                                        string.Format("调整{0}的多仓止损参考价为{1}", pDepthMarketData.InstrumentID,
                                            averageLastPrice),
                                        true);
                                }

                                if (stopLossPrices.ForShort > averageLastPrice) //随着最新价的走低，不断降低空仓的止损参考价
                                {
                                    stopLossPrices.ForShort = averageLastPrice;
                                    WriteLine(
                                        string.Format("调整{0}的空仓止损参考价为{1}", pDepthMarketData.InstrumentID,
                                            averageLastPrice),
                                        true);
                                }
                            }
                            else
                            {
                                WriteLine(string.Format("忽略{0}的异常移动均价{1}", instrumentId, averageLastPrice), true);
                            }
                        }
                        else
                        {
                            var stopLossPrices = CreateStopLossPrices(pDepthMarketData);
                            InstrumentToStopLossPrices[pDepthMarketData.InstrumentID] = stopLossPrices;
                        }
                    }
                }

                var s = FormatQuote(pDepthMarketData, xianshou, cangcha, xingzhi);

                try
                {
                    LogQuotes[GetInstrumentCategory(pDepthMarketData.InstrumentID)].Debug(s);
                }
                catch (Exception)
                {

                }

                Console.WriteLine(s);
            }
            catch (Exception ex)
            {
                WriteException(ex);
            }

        }

        public static double GetAveragePrice(ThostFtdcDepthMarketDataField data)
        {
            return data.AveragePrice/data.PreClosePrice < 2
                ? data.AveragePrice
                : InstrumentToInstrumentInfo.ContainsKey(data.InstrumentID)
                    ? data.AveragePrice/InstrumentToInstrumentInfo[data.InstrumentID].VolumeMultiple
                    : data.AveragePrice;
        }

        public static void WriteException(Exception ex)
        {
            WriteLine(ex.Source + ex.Message + ex.StackTrace, true);
        }

        public static void WriteLine(string line = "\n", bool writeInfo = false)
        {
            try
            {
                LogDebug.Debug(line);
                if (writeInfo)
                {
                    LogInfo.Debug(line);
                }
                Console.WriteLine(line);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Source + ex.Message + ex.StackTrace);
            }
        }

        public static void OutputLine()
        {
            WriteLine("********************************************************");
        }

        public static string OutputField(object obj, bool outputToFile = true, bool writeInfo = false)
        {
            var sb = new StringBuilder();

            if (outputToFile)
            {
                WriteLine("\n", writeInfo);
                OutputLine();
            }

            var type = obj.GetType();
            var fields = type.GetFields();

            foreach (var field in fields)
            {
                var temp = string.Format("[{0}]:[{1}]", field.Name, field.GetValue(obj));

                if (outputToFile)
                {
                    WriteLine(temp, writeInfo);
                }

                sb.AppendLine(temp);
            }

            if (outputToFile)
            {
                OutputLine();
                WriteLine("\n", writeInfo);
            }

            return sb.ToString();
        }

        public static bool IsWrongRspInfo(ThostFtdcRspInfoField pRspInfo)
        {
            return pRspInfo != null && pRspInfo.ErrorID != 0;
        }

        public static void ReportError(ThostFtdcRspInfoField pRspInfo, string title)
        {
            if (IsWrongRspInfo(pRspInfo))
            {
                var message = string.Format("{0}:{1}", title, pRspInfo.ErrorMsg);
                WriteLine(message, true);
                Email.SendMail(message, DateTime.Now.ToString(CultureInfo.InvariantCulture));
            }
        }


        public static bool IsCorrectRspInfo(ThostFtdcRspInfoField pRspInfo)
        {
            return pRspInfo != null && pRspInfo.ErrorID == 0;
        }

        /// <summary>
        /// 读取程序的配置参数
        /// </summary>
        public static void ReadConfig()
        {
            var configFile = AssemblyPath + "config.ini";

            if (File.Exists(configFile))
            {
                var sr = new StreamReader(configFile, Encoding.UTF8);

                var line = sr.ReadLine();

                var s = GetLineData(line).Split(new[] {";"}, StringSplitOptions.RemoveEmptyEntries);
                AllowedCategories.AddRange(s.Where(t => !string.IsNullOrWhiteSpace(t)));

                line = sr.ReadLine();

                GoUpRangeComparedToPreClosePrice = 1 + Convert.ToDouble(GetLineData(line));
                FallDownRangeComparedToPreClosePrice = 1 - Convert.ToDouble(GetLineData(line));

                line = sr.ReadLine();

                GoUpRangeComparedToLowestPrice = 1 + Convert.ToDouble(GetLineData(line));
                FallDonwRangeComparedToHighestPrice = 1 - Convert.ToDouble(GetLineData(line));

                line = sr.ReadLine();
                StopLossUpperRange = 1 + Convert.ToDouble(GetLineData(line));
                StopLossLowerRange = 1 - Convert.ToDouble(GetLineData(line));

                line = sr.ReadLine();
                LimitCloseRange = 1 - Convert.ToDouble(GetLineData(line));

                line = sr.ReadLine();
                OpenVolumePerTime = Convert.ToInt32(GetLineData(line));

                line = sr.ReadLine();
                CategoryUpperLimit = Convert.ToInt32(GetLineData(line));

                line = sr.ReadLine();
                OpenTolerance = Convert.ToDouble(GetLineData(line));

                line = sr.ReadLine();
                CloseTolerance = Convert.ToDouble(GetLineData(line));

                line = sr.ReadLine();
                CurrentDistanceToHighestDistanceRatioLimit = Convert.ToDouble(GetLineData(line));

                line = sr.ReadLine();
                MinuteByMinuteSizeShort = Convert.ToInt32(GetLineData(line));

                line = sr.ReadLine();
                MinuteByMinuteSizeLong = Convert.ToInt32(GetLineData(line));

                line = sr.ReadLine();
                HighestDistanceConsiderLimit = Convert.ToDouble(GetLineData(line));

                line = sr.ReadLine();
                InstrumentTotalPrice = Convert.ToDouble(GetLineData(line));

                line = sr.ReadLine();
                MinuteByMinuteSizeMiddle = Convert.ToInt32(GetLineData(line));

                line = sr.ReadLine();
                MathUtils.Slope = Convert.ToDouble(GetLineData(line));

                line = sr.ReadLine();
                SimNowAccount = GetLineData(line);

                line = sr.ReadLine();
                SimNowPassword = GetLineData(line);

                line = sr.ReadLine();
                MathUtils.Slope2 = Convert.ToDouble(GetLineData(line));

                line = sr.ReadLine();
                OpenAccordingToTrendLimit = Convert.ToDouble(GetLineData(line));

                line = sr.ReadLine();
                SwingLimit = Convert.ToDouble(GetLineData(line));

                line = sr.ReadLine();
                StopProfitRatio = Convert.ToDouble(GetLineData(line));
                sr.Close();
            }
        }

        public static string GetLineData(string line)
        {
            var s = line.Split(new[] {'#'}, StringSplitOptions.RemoveEmptyEntries);

            if (s.Length > 0)
            {
                return s[0].Trim();
            }

            return null;
        }

        /// <summary>
        /// 根据合约名、持仓方向、昨仓今仓，生成该仓位的键
        /// </summary>
        /// <param name="instrumentId"></param>
        /// <param name="direction"></param>
        /// <param name="positionDate"></param>
        /// <returns></returns>
        public static string GetPositionKey(string instrumentId, EnumPosiDirectionType direction,
            EnumPositionDateType positionDate)
        {
            return string.Format("{0}:{1}:{2}", instrumentId, direction, positionDate);
        }

        public static string GetOpenTrendStartPointKey(string instrumentId, EnumPosiDirectionType direction)
        {
            return string.Format("{0}:{1}", instrumentId, direction);
        }

        public static void SetOpenTrendStartPoint(string instrumentId, EnumPosiDirectionType longOrShort,
            double openTrendStartPoint)
        {
            InstrumentToOpenTrendStartPoint[GetOpenTrendStartPointKey(instrumentId, longOrShort)] = openTrendStartPoint;
            WriteLine(string.Format("设置{0}的开{1}仓的趋势启动点为{2}", instrumentId, longOrShort, openTrendStartPoint), true);
        }

        /// <summary>
        /// 根据合约名、开仓方向，生成开仓操作的键
        /// </summary>
        /// <param name="instrumentId"></param>
        /// <param name="buyOrSell"></param>
        /// <returns></returns>
        public static string GetOpenPositionKey(string instrumentId, EnumDirectionType buyOrSell)
        {
            return string.Format("{0}:{1}", instrumentId, buyOrSell);
        }

        public static string GetInstrumentIdFromPositionKey(string positionKey)
        {
            var s = positionKey.Split(new[] {':'}, StringSplitOptions.RemoveEmptyEntries);
            return s[0];
        }

        public static void SetMissedOpenStartPoint(string instrumentId, EnumPosiDirectionType longOrShort,
            double openTrendStartPoint)
        {
            var buyOrSell = longOrShort == EnumPosiDirectionType.Long
                ? EnumDirectionType.Buy
                : EnumDirectionType.Sell;
            var keyMissedOpenTrendStartPoint = GetOpenPositionKey(instrumentId, buyOrSell);

            if (InstrumentToMissedOpenTrendStartPoint.ContainsKey(keyMissedOpenTrendStartPoint))
            {
                double minMax;
                if (longOrShort == EnumPosiDirectionType.Long)
                {
                    minMax = Math.Min(openTrendStartPoint,
                        InstrumentToMissedOpenTrendStartPoint[keyMissedOpenTrendStartPoint]);
                }
                else
                {
                    minMax = Math.Max(openTrendStartPoint,
                        InstrumentToMissedOpenTrendStartPoint[keyMissedOpenTrendStartPoint]);
                }

                InstrumentToMissedOpenTrendStartPoint[keyMissedOpenTrendStartPoint] = minMax;
                WriteLine(string.Format("设置{0}的{1}仓的缺失趋势启动点为{2}", instrumentId, longOrShort, minMax), true);
            }
            else
            {
                InstrumentToMissedOpenTrendStartPoint[keyMissedOpenTrendStartPoint] = openTrendStartPoint;
                WriteLine(string.Format("新建{0}的{1}仓的缺失趋势启动点为{2}", instrumentId, longOrShort, openTrendStartPoint), true);
            }
        }

        /// <summary>
        /// 判断该合约是不是允许交易的品种以及是不是主力合约
        /// </summary>
        /// <param name="instrumentId"></param>
        /// <returns></returns>
        public static bool IsTradableInstrument(string instrumentId)
        {
            if (CategoryToMainInstrument.ContainsKey(GetInstrumentCategory(instrumentId)))
            {
                var v = CategoryToMainInstrument[GetInstrumentCategory(instrumentId)];

                if (v.Equals(instrumentId))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 判断当前时间是否在合约的交易时间之内
        /// </summary>
        /// <param name="instrumentId"></param>
        /// <returns></returns>
        public static bool IsInInstrumentTradingTime(string instrumentId)
        {
            if (CurrentChannel == ChannelType.模拟24X7)
            {
                return true;
            }

            var category = GetInstrumentCategory(instrumentId);

            if (CategoryToExchangeId.ContainsKey(category))
            {
                return ExchangeTime.Instance.IsTradingTime(category, CategoryToExchangeId[category]);
            }

            WriteLine(string.Format("当前时段{0}不是合约{1}的交易时间段", DateTime.Now, instrumentId), true);
            return false;
        }

        /// <summary>
        /// 判断合约是否有未完成的报单
        /// </summary>
        /// <param name="instrumentId"></param>
        /// <param name="direction"></param>
        /// <param name="openOrClose"></param>
        /// <returns></returns>
        public static bool IsUnFinishedOrderExisting(string instrumentId, EnumDirectionType direction,
            EnumOffsetFlagType openOrClose)
        {
            if (
                OrderRefToUnFinishedOrders.Values.Any(
                    s =>
                        s.InstrumentID.Equals(instrumentId) && s.Direction == direction &&
                        s.CombOffsetFlag_0 == openOrClose)) //该合约还有未完成的同方向报单，不报单
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// 判断合约是不是上期所的合约
        /// </summary>
        /// <param name="instrumentId"></param>
        /// <returns></returns>
        public static bool IsShfeInstrument(string instrumentId)
        {
            var category = GetInstrumentCategory(instrumentId);

            if (CategoryToExchangeId.ContainsKey(category))
            {
                return CategoryToExchangeId[category].Equals("SHFE");
            }

            return false;
        }

        public static StopLossPrices CreateStopLossPrices(ThostFtdcDepthMarketDataField pDepthMarketData)
        {
            try
            {
                //新交易日未开盘时，最高价和最低价为无效值，要排除；交易日中途启动时，暂时设最高价最低价，其实应该读取上次的参考价。
                var stopLossPrices = new StopLossPrices {Instrument = pDepthMarketData.InstrumentID};

                if (pDepthMarketData.HighestPrice > 1 && pDepthMarketData.LowestPrice > 1)
                {
                    stopLossPrices.ForLong = pDepthMarketData.HighestPrice;
                    stopLossPrices.ForShort = pDepthMarketData.LowestPrice;
                }
                else
                {
                    stopLossPrices.ForLong = pDepthMarketData.PreClosePrice;
                    stopLossPrices.ForShort = pDepthMarketData.PreClosePrice;
                }

                return stopLossPrices;
            }
            catch (Exception ex)
            {
                WriteException(ex);
            }
            return null;
        }

        public static bool IsInstrumentLocked(string instrumentId)
        {
            if (LockedInstruments.ContainsKey(instrumentId))
            {
                WriteLine(string.Format("合约{0}还有报单在途中，不报单", instrumentId), true);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 锁定正在开仓的合约
        /// </summary>
        /// <param name="instrumentId"></param>
        public static void LockOpenInstrument(string instrumentId)
        {
            LockedOpenInstruments[instrumentId] = instrumentId;
            WriteLine(string.Format("增加开仓在途记录{0}", instrumentId), true);
        }

        public static void RemoveLockedOpenInstrument(string instrumentId)
        {
            if (LockedOpenInstruments.ContainsKey(instrumentId))
            {
                string temp;
                LockedOpenInstruments.TryRemove(instrumentId, out temp);
                WriteLine(string.Format("减少开仓在途记录{0}", instrumentId), true);
            }
        }

        /// <summary>
        /// 锁定正在报单的合约
        /// </summary>
        /// <param name="instrumentId"></param>
        public static void LockInstrument(string instrumentId)
        {
            LockedInstruments[instrumentId] = instrumentId;
            WriteLine(string.Format("锁定{0}", instrumentId), true);
        }

        public static void RemoveLockedInstrument(string instrumentId)
        {
            if (LockedInstruments.ContainsKey(instrumentId))
            {
                string temp;
                LockedInstruments.TryRemove(instrumentId, out temp);
                WriteLine(string.Format("解锁{0}", instrumentId), true);
            }
        }

        public static void UnlockInstrument(string instrumentId, EnumOffsetFlagType flag)
        {
            if (flag == EnumOffsetFlagType.Open)
            {
                RemoveLockedOpenInstrument(instrumentId);
            }

            RemoveLockedInstrument(instrumentId);
        }

        /// <summary>
        /// 读取止损参考价
        /// </summary>
        public static void ReadStopLossPrices()
        {
            var file = string.Format(AssemblyPath + "{0}_StopLossPrices.txt", CurrentChannel);

            if (File.Exists(file))
            {
                var sr = new StreamReader(file, Encoding.UTF8);
                string line;

                while ((line = sr.ReadLine()) != null)
                {
                    var s = line.Split(new[] {":"}, StringSplitOptions.RemoveEmptyEntries);

                    if (s.Length > 4)
                    {
                        string instrument;
                        double costLong;
                        double costShort;
                        double forLong;
                        double forShort;

                        try
                        {
                            instrument = s[0];
                        }
                        catch (Exception)
                        {
                            instrument = null;
                        }

                        try
                        {
                            costLong = Convert.ToDouble(s[1]);
                        }
                        catch (Exception)
                        {
                            costLong = 0;
                        }

                        try
                        {
                            costShort = Convert.ToDouble(s[2]);
                        }
                        catch (Exception)
                        {
                            costShort = 0;
                        }

                        try
                        {
                            forLong = Convert.ToDouble(s[3]);
                        }
                        catch (Exception)
                        {
                            forLong = 0;
                        }

                        try
                        {
                            forShort = Convert.ToDouble(s[4]);
                        }
                        catch (Exception)
                        {
                            forShort = 0;
                        }

                        var stopLossPrices = new StopLossPrices
                        {
                            Instrument = instrument,
                            CostLong = costLong,
                            CostShort = costShort,
                            ForLong = forLong,
                            ForShort = forShort
                        };
                        InstrumentToStopLossPrices[s[0]] = stopLossPrices;
                        WriteLine(
                            string.Format("读取合约{0},多仓成本价{1},空仓成本价{2},多仓止损价{3},空仓止损价{4}", stopLossPrices.Instrument,
                                stopLossPrices.CostLong, stopLossPrices.CostShort, stopLossPrices.ForLong,
                                stopLossPrices.ForShort), true);
                    }
                }

                sr.Close();
            }
        }

        public static void SaveInstrumentTotalPrices()
        {
            try
            {
                //保存一手合约的当前总价
                if (CategoryToMainInstrument.Count > 0)
                {
                    var sw = new StreamWriter(AssemblyPath + string.Format("{0}_InstrumentPrices.txt", CurrentChannel),
                        false, Encoding.UTF8);
                    var sb = new StringBuilder();
                    foreach (var kv in CategoryToMainInstrument)
                    {
                        if (InstrumentToInstrumentInfo.ContainsKey(kv.Value) &&
                            InstrumentToQuotes.ContainsKey(kv.Value))
                        {
                            var instrumentInfo = InstrumentToInstrumentInfo[kv.Value];
                            var quotes = InstrumentToQuotes[kv.Value];
                            if (quotes.Count > 0)
                            {
                                var quote = quotes[quotes.Count - 1];
                                var totalPrice = instrumentInfo.VolumeMultiple*quote.LastPrice;
                                InstrumentToTotalPrice[kv.Value] = totalPrice;
                                var temp = string.Format("{0}:{1}:{2}", kv.Key, kv.Value, totalPrice);
                                sb.AppendLine(temp);
                            }
                        }
                    }

                    sw.WriteLine(sb);
                    sw.Close();
                }
            }
            catch (Exception ex)
            {
                WriteException(ex);
            }

        }

        public static void Exit(object trader = null)
        {
            try
            {
                if (trader != null)
                {
                    ((ITraderAdapter)trader).CloseAllPositions();
                    SaveStopLossPrices();
                }

                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                WriteException(ex);
            }

        }

        /// <summary>
        /// 保存止损参考价
        /// </summary>
        public static void SaveStopLossPrices()
        {
            try
            {
                //保存止损价
                if (InstrumentToStopLossPrices.Count > 0)
                {
                    var sw = new StreamWriter(string.Format("{0}_StopLossPrices.txt", CurrentChannel), false,
                        Encoding.UTF8);
                    var sb = new StringBuilder();
                    foreach (var kv in InstrumentToStopLossPrices)
                    {
                        var temp = string.Format("{0}:{1}:{2}:{3}:{4}", kv.Key, kv.Value.CostLong, kv.Value.CostShort,
                            kv.Value.ForLong, kv.Value.ForShort);
                        sb.AppendLine(temp);
                    }

                    sw.WriteLine(sb);
                    WriteLine("保存止损价" + sb);
                    sw.Close();
                }
            }
            catch (Exception ex)
            {
                WriteException(ex);
            }

        }

        /// <summary>
        /// 恢复合约的当前最高价最低价为初始值
        /// </summary>
        public static void RestoreInstrumentToMaxAndMinPrice(string instrumentId)
        {
            if (InstrumentToMaxAndMinPrice.ContainsKey(instrumentId))
            {
                InstrumentToMaxAndMinPrice[instrumentId].MinPrice = LargeNumber;
                InstrumentToMaxAndMinPrice[instrumentId].MaxPrice = 0;
                WriteLine(string.Format("已执行平仓，恢复{0}的当前最大值为{1}，最小值为{2}", instrumentId, 0, LargeNumber), true);
            }
        }

        public static ConcurrentDictionary<string, double> InstrumentToTotalPrice =
            new ConcurrentDictionary<string, double>();

        /// <summary>
        /// 所有合约行情，最后一个是最新行情
        /// </summary>
        public static ConcurrentDictionary<string, List<ThostFtdcDepthMarketDataField>> InstrumentToQuotes =
            new ConcurrentDictionary<string, List<ThostFtdcDepthMarketDataField>>();

        public static ConcurrentDictionary<string, ThostFtdcInstrumentField> InstrumentToInstrumentInfo =
            new ConcurrentDictionary<string, ThostFtdcInstrumentField>();

        public static ConcurrentDictionary<string, List<ThostFtdcDepthMarketDataField>> InstrumentToInstrumentsDepthMarketData =
                new ConcurrentDictionary<string, List<ThostFtdcDepthMarketDataField>>();

        public static ConcurrentDictionary<string, string> CategoryToMainInstrument =
            new ConcurrentDictionary<string, string>();

        public static ConcurrentDictionary<string, string> CategoryToExchangeId =
            new ConcurrentDictionary<string, string>();

        public static ConcurrentDictionary<string, ThostFtdcOrderField> OrderRefToUnFinishedOrders =
            new ConcurrentDictionary<string, ThostFtdcOrderField>();

        /// <summary>
        /// 合约的止损参考价，分为多仓和空仓的成本价，多仓和空仓的止损参考价
        /// </summary>
        public static ConcurrentDictionary<string, StopLossPrices> InstrumentToStopLossPrices =
            new ConcurrentDictionary<string, StopLossPrices>();

        /// <summary>
        /// 还未收到报单响应的合约，暂时不能报单
        /// </summary>
        public static ConcurrentDictionary<string, string> LockedInstruments =
            new ConcurrentDictionary<string, string>();

        /// <summary>
        /// 记录开仓在途的合约，防止开仓超过品种数量限制
        /// </summary>
        public static ConcurrentDictionary<string, string> LockedOpenInstruments =
            new ConcurrentDictionary<string, string>();

        /// <summary>
        /// 合约的开仓次数记录，如果超过，不再开仓
        /// </summary>
        public static ConcurrentDictionary<string, int> InstrumentToOpenCount = new ConcurrentDictionary<string, int>();

        /// <summary>
        /// 合约的分时图数据，取每分钟最后一个行情数据
        /// </summary>
        public static ConcurrentDictionary<string, List<Tuple<string, Quote>>> InstrumentToMinuteByMinuteChart
            = new ConcurrentDictionary<string, List<Tuple<string, Quote>>>();

        /// <summary>
        /// 记录上次合约的买入平仓、卖出平仓时间，防止刚刚平仓又立即开同方向的仓
        /// </summary>
        public static ConcurrentDictionary<string, DateTime> InstrumentToLastCloseTime =
            new ConcurrentDictionary<string, DateTime>();

        public static ConcurrentDictionary<string, ThostFtdcDepthMarketDataField> InstrumentToLastTick =
            new ConcurrentDictionary<string, ThostFtdcDepthMarketDataField>();

        /// <summary>
        /// 记录合约开仓的趋势启动点，分为多仓和空仓
        /// </summary>
        public static ConcurrentDictionary<string, double> InstrumentToOpenTrendStartPoint =
            new ConcurrentDictionary<string, double>();

        /// <summary>
        /// 记录合约开仓被禁止时丢失的趋势启动点，分为多仓和空仓
        /// </summary>
        public static ConcurrentDictionary<string, double> InstrumentToMissedOpenTrendStartPoint =
            new ConcurrentDictionary<string, double>();

        /// <summary>
        /// 记录合约从程序启动时到目前的最高价、最低价，以便计算振幅
        /// </summary>
        public static ConcurrentDictionary<string, MaxAndMinPrice> InstrumentToMaxAndMinPrice =
            new ConcurrentDictionary<string, MaxAndMinPrice>();

        /// <summary>
        /// 记录合约上一笔是多仓还是空仓
        /// </summary>
        public static ConcurrentDictionary<string, EnumPosiDirectionType> InstrumentToLastPosiDirectionType =
            new ConcurrentDictionary<string, EnumPosiDirectionType>();


        /// <summary>
        /// 记录合约开仓时刻的角度
        /// </summary>
        public static ConcurrentDictionary<string, double> InstrumentToOpenAngle =
            new ConcurrentDictionary<string, double>();
    }


    public class ExchangeTime
    {
        private ExchangeTime()
        {
            try
            {
                //无夜盘的品种
                m_sExchsh = "SHFE 9:0:0-10:15:0;10:30:0-11:30:0;13:30:0-15:0:0";
                m_sExchdl = "DCE 9:0:0-10:15:0;10:30:0-11:30:0;13:30:0-15:0:0";
                m_sExchzz = "CZCE 9:0:0-10:15:0;10:30:0-11:30:0;13:30:0-15:0:0";

                //所有的夜盘
                m_sExchzzNight = "RM;FG;MA;SR;TA;ZC;CF; 21:0:0-23:30:0";
                m_sExchdlNight = "i;j;jm;a;m;p;y; 21:0:0-23:30:0";
                m_sExchshNight1 = "ag;au; 21:0:0-23:59:59;0:0:0-2:30:0";
                m_sExchshNight3 = "rb;bu;hc 21:0:0-23:0:0";

                m_mapTradingTime = new Dictionary<string, List<TradingTime>>();
                m_sLog = "交易时间";

                InitDefault();
            }
            catch (Exception ex)
            {
            }
        }

        public static ExchangeTime Instance
        {
            get { return _exchangeTime; }
        }

        // sCate在交易时间段返回true
        public bool IsTradingTime(string category, string exchange)
        {
            var result = true;
            try
            {
                foreach (var kvp in m_mapTradingTime)
                {
                    var s = new List<string>();

                    if (kvp.Key.Contains(";"))
                    {
                        var instrumentIds = kvp.Key.Split(new[] {';'}, StringSplitOptions.RemoveEmptyEntries);
                        s.AddRange(instrumentIds);
                    }

                    if (s.Contains(category) || kvp.Key.Equals(exchange))
                    {
                        var currentDateTime = DateTime.Now;
                        var currentSecond = GetSecFromDateTime(currentDateTime) + Utils.ExchangeTimeOffset;
                        foreach (var time in kvp.Value)
                        {
                            if ((currentSecond >= time.StartSecond) && (currentSecond <= time.EndSecond))
                            {
                                return true;
                            }
                        }
                    }

                    result = false;
                }
            }
            catch (Exception ex)
            {
                return true;
            }

            return result;
        }

        private void ParseFromString(string sLine)
        {
            try
            {
                var sKeyValue = sLine.Split(' ');
                var sKey = sKeyValue[0];
                m_sLog += sKey;

                var sValueString = sKeyValue[1];
                var sValue = sValueString.Split(';');
                foreach (var s in sValue)
                {
                    var s1 = s.Split('-');
                    var dtStart = Convert.ToDateTime(s1[0]);
                    var dtEnd = Convert.ToDateTime(s1[1]);
                    var time = new TradingTime
                    {
                        StartTimeString = dtStart.ToLongTimeString(),
                        EndTimeString = dtEnd.ToLongTimeString(),
                        StartSecond = GetSecFromDateTime(dtStart),
                        EndSecond = GetSecFromDateTime(dtEnd)
                    };
                    if (!m_mapTradingTime.ContainsKey(sKey))
                    {
                        m_mapTradingTime[sKey] = new List<TradingTime>();
                    }
                    m_mapTradingTime[sKey].Add(time);
                    m_sLog += s1[0];
                    m_sLog += s1[1];
                }
            }
            catch (Exception ex)
            {
            }
        }

        public int GetSecFromDateTime(DateTime dt)
        {
            try
            {
                return dt.Hour*3600 + dt.Minute*60 + dt.Second;
            }
            catch (Exception ex)
            {
            }
            return 0;
        }

        private void InitDefault()
        {
            try
            {
                if (m_mapTradingTime.Count > 0)
                    m_mapTradingTime.Clear();

                ParseFromString(m_sExchsh);
                ParseFromString(m_sExchdl);
                ParseFromString(m_sExchzz);
                ParseFromString(m_sExchzzNight);
                ParseFromString(m_sExchdlNight);
                ParseFromString(m_sExchshNight1);
                ParseFromString(m_sExchshNight2);
                ParseFromString(m_sExchshNight3);
            }
            catch (Exception ex)
            {
            }
        }

        private static readonly ExchangeTime _exchangeTime = new ExchangeTime();

        private string m_sLog;
        private readonly string m_sExchsh; // 上海
        private readonly string m_sExchdl; // 大连
        private readonly string m_sExchzz; // 郑州

        private readonly string m_sExchzzNight; // TA;SR;CF;RM;ME;MA夜盘
        private readonly string m_sExchdlNight; // p;j;a;b;m;y;jm;i夜盘
        private readonly string m_sExchshNight1; // ag;au夜盘
        private string m_sExchshNight2; // cu;al;zn;pb;rb;hc;bu夜盘
        private string m_sExchshNight3; // ru夜盘

        private Dictionary<string, List<TradingTime>> m_mapTradingTime;
    }

    public class TradingTime
    {
        public string StartTimeString;
        public string EndTimeString;
        public int StartSecond; // 时间段开始时间的秒数
        public int EndSecond;
    }

    public class StopLossPrices
    {
        /// <summary>
        /// 合约
        /// </summary>
        public string Instrument;

        /// <summary>
        /// 当前多仓的持仓成本价
        /// </summary>
        public double CostLong;

        /// <summary>
        /// 当前空仓的持仓成本价
        /// </summary>
        public double CostShort;

        /// <summary>
        /// 多仓止损参考价
        /// </summary>
        public double ForLong;

        /// <summary>
        /// 空仓止损参考价
        /// </summary>
        public double ForShort;
    }

    public class Quote
    {
        public ThostFtdcDepthMarketDataField MarketData;

        public double LastPrice;

        public double AveragePrice;

        /// <summary>
        /// 最新价和平均价的距离,LastPrice - AveragePrice;
        /// </summary>
        public double Distance
        {
            get { return LastPrice - AveragePrice; }
        }
    }

    public class MaxAndMinPrice
    {
        public double MaxPrice;
        public double MinPrice;

        public double Swing
        {
            get { return MaxPrice - MinPrice; }
        }
    }
}

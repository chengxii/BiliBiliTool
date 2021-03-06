﻿using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Interfaces;
using Ray.BiliBiliTool.Config;
using Ray.BiliBiliTool.Config.Options;
using Ray.BiliBiliTool.DomainService.Interfaces;

namespace Ray.BiliBiliTool.DomainService
{
    /// <summary>
    /// 投币
    /// </summary>
    public class DonateCoinDomainService : IDonateCoinDomainService
    {
        private readonly ILogger<DonateCoinDomainService> _logger;
        private readonly IDailyTaskApi _dailyTaskApi;
        private readonly BiliBiliCookieOptions _biliBiliCookieOptions;
        private readonly DailyTaskOptions _dailyTaskOptions;
        private readonly IAccountApi _accountApi;
        private readonly ICoinDomainService _coinDomainService;
        private readonly IVideoDomainService _videoDomainService;
        private readonly IRelationApi _relationApi;

        private readonly Dictionary<string, int> _alreadyDonatedCoinsCatch = new Dictionary<string, int>();

        public DonateCoinDomainService(ILogger<DonateCoinDomainService> logger,
            IDailyTaskApi dailyTaskApi,
            IOptionsMonitor<BiliBiliCookieOptions> cookieOptions,
            IOptionsMonitor<DailyTaskOptions> dailyTaskOptions,
            IAccountApi accountApi,
            ICoinDomainService coinDomainService,
            IVideoDomainService videoDomainService,
            IRelationApi relationApi)
        {
            _logger = logger;
            _dailyTaskApi = dailyTaskApi;
            _biliBiliCookieOptions = cookieOptions.CurrentValue;
            _dailyTaskOptions = dailyTaskOptions.CurrentValue;
            _accountApi = accountApi;
            _coinDomainService = coinDomainService;
            _videoDomainService = videoDomainService;
            _relationApi = relationApi;
        }

        /// <summary>
        /// 完成投币任务
        /// </summary>
        public void AddCoinsForVideo()
        {
            int needCoins = GetNeedDonateCoinNum();
            if (needCoins <= 0) return;

            //投币前硬币余额
            decimal coinBalance = _coinDomainService.GetCoinBalance();
            _logger.LogInformation("投币前余额为 : {coinBalance}", coinBalance);

            if (coinBalance <= 0)
            {
                _logger.LogInformation("因硬币余额不足，今日暂不执行投币任务");
                return;
            }

            //余额小于目标投币数，按余额投
            if (coinBalance < needCoins)
            {
                int.TryParse(decimal.Truncate(coinBalance).ToString(), out needCoins);
                _logger.LogInformation("因硬币余额不足，目标投币数调整为: {needCoins}", needCoins);
            }

            for (int i = 0; i < needCoins; i++)
            {
                Tuple<string, string> video = TryGetCanDonatedVideo();
                if (video == null) continue;

                _logger.LogDebug("正在为视频“{title}”投币", video.Item2);

                DoAddCoinForVideo(video.Item1, 1, _dailyTaskOptions.SelectLike, video.Item2);
            }

            _logger.LogInformation("投币任务完成，余额为: {money}", _accountApi.GetCoinBalance().Result.Data.Money);

        }

        /// <summary>
        /// 尝试获取一个可以投币的视频
        /// </summary>
        /// <returns></returns>
        public Tuple<string, string> TryGetCanDonatedVideo()
        {
            Tuple<string, string> result = null;

            //如果配置upID，则从up中随机尝试获取5次
            if (_dailyTaskOptions.SupportUpIdList.Count > 0)
            {
                result = TryGetCanDonatedVideoByUp(5);
                if (result != null) return result;
            }

            //然后从特别关注列表尝试获取5次
            result = TryGetCanDonatedVideoBySpecialUps(5);
            if (result != null) return result;

            //然后从普通关注列表获取5次
            result = TryGetCanDonatedVideoByFollowingUps(5);
            if (result != null) return result;

            //最后从排行榜尝试5次
            result = TryGetNotDonatedVideoByRegion(5);

            return result;
        }

        /// <summary>
        /// 为视频投币
        /// </summary>
        /// <param name="aid">av号</param>
        /// <param name="multiply">投币数量</param>
        /// <param name="select_like">是否同时点赞 1是0否</param>
        /// <returns>是否投币成功</returns>
        public bool DoAddCoinForVideo(string aid, int multiply, bool select_like, string title = "")
        {
            BiliApiResponse result = _dailyTaskApi.AddCoinForVideo(aid, multiply, select_like ? 1 : 0, _biliBiliCookieOptions.BiliJct).Result;

            if (result.Code == 0)
            {
                _logger.LogInformation("为“{title}”投币成功", title);
                return true;
            }

            if (result.Code == -111)
            {
                string errorMsg = $"投币异常，Cookie配置项[BiliJct]错误或已过期，请检查并更新。接口返回：{result.Message}";
                _logger.LogError(errorMsg);
                throw new Exception(errorMsg);
            }
            else
            {
                _logger.LogInformation("为“{title}”投币失败，原因：{msg}", title, result.Message);
                return false;
            }
        }

        #region private

        /// <summary>
        /// 获取今日的目标投币数
        /// </summary>
        /// <param name="alreadyCoins"></param>
        /// <param name="targetCoins"></param>
        /// <returns></returns>
        private int GetNeedDonateCoinNum()
        {
            //获取自定义配置投币数
            int configCoins = _dailyTaskOptions.NumberOfCoins;
            //已投的硬币
            int alreadyCoins = _coinDomainService.GetDonatedCoins();
            //目标
            int targetCoins = configCoins > Constants.MaxNumberOfDonateCoins
                ? Constants.MaxNumberOfDonateCoins
                : configCoins;
            _logger.LogInformation("今日已投{already}枚硬币，目标是投{target}枚硬币", alreadyCoins, targetCoins);

            if (targetCoins > alreadyCoins)
            {
                int needCoins = targetCoins - alreadyCoins;
                _logger.LogInformation("还需再投{need}枚硬币", needCoins);
                return needCoins;
            }

            _logger.LogInformation("已经完成投币任务，今天不需要再投啦");
            return 0;
        }

        /// <summary>
        /// 尝试从配置的up主里随机获取一个可以投币的视频
        /// </summary>
        /// <param name="tryCount"></param>
        /// <returns></returns>
        private Tuple<string, string> TryGetCanDonatedVideoByUp(int tryCount)
        {
            //是否配置了up主
            if (_dailyTaskOptions.SupportUpIdList.Count == 0) return null;

            return TryGetCanDonateVideoByUps(_dailyTaskOptions.SupportUpIdList, tryCount); ;
        }

        /// <summary>
        /// 尝试从特别关注的Up主中随机获取一个可以投币的视频
        /// </summary>
        /// <param name="tryCount"></param>
        /// <returns></returns>
        private Tuple<string, string> TryGetCanDonatedVideoBySpecialUps(int tryCount)
        {
            //获取特别关注列表
            BiliApiResponse<List<UpInfo>> specials = _relationApi.GetSpecialFollowings().Result;
            if (specials.Data == null || specials.Data.Count == 0) return null;

            return TryGetCanDonateVideoByUps(specials.Data.Select(x => x.Mid).ToList(), tryCount);
        }

        /// <summary>
        /// 尝试从普通关注的Up主中随机获取一个可以投币的视频
        /// </summary>
        /// <param name="tryCount"></param>
        /// <returns></returns>
        private Tuple<string, string> TryGetCanDonatedVideoByFollowingUps(int tryCount)
        {
            //获取特别关注列表
            BiliApiResponse<GetFollowingsResponse> result = _relationApi.GetFollowings(_biliBiliCookieOptions.UserId).Result;
            if (result.Data.Total == 0) return null;

            return TryGetCanDonateVideoByUps(result.Data.List.Select(x => x.Mid).ToList(), tryCount);
        }

        /// <summary>
        /// 尝试从排行榜中获取一个没有看过的视频
        /// </summary>
        /// <param name="tryCount"></param>
        /// <returns></returns>
        private Tuple<string, string> TryGetNotDonatedVideoByRegion(int tryCount)
        {
            if (tryCount <= 0) return null;

            for (int i = 0; i < tryCount; i++)
            {
                Tuple<string, string> video = _videoDomainService.GetRandomVideoOfRegion();
                if (!CanDonatedCoinsForVideo(video.Item1)) continue;
                return video;
            }

            return null;
        }

        /// <summary>
        /// 尝试从指定的up主Id集合中随机获取一个可以投币的视频
        /// </summary>
        /// <param name="upIds"></param>
        /// <param name="tryCount"></param>
        /// <returns></returns>
        private Tuple<string, string> TryGetCanDonateVideoByUps(List<long> upIds, int tryCount)
        {
            //缓存每个up的视频总数
            Dictionary<long, int> videoCountDic = new Dictionary<long, int>();

            //获取特别关注列表
            if (upIds == null || upIds.Count == 0) return null;

            //尝试tryCount次
            for (int i = 1; i <= tryCount; i++)
            {
                //获取随机Up主Id
                long randomUpId = upIds[new Random().Next(0, upIds.Count)];

                //该up的视频总数
                if (!videoCountDic.TryGetValue(randomUpId, out int videoCount))
                {
                    videoCount = _videoDomainService.GetVideoCountOfUp(randomUpId);
                    videoCountDic.Add(randomUpId, videoCount);
                }
                if (videoCount == 0 | videoCount < i) continue;

                UpVideoInfo videoInfo = _videoDomainService.GetRandomVideoOfUp(randomUpId, videoCount);

                if (!CanDonatedCoinsForVideo(videoInfo.Aid.ToString())) continue;
                return Tuple.Create(videoInfo.Aid.ToString(), videoInfo.Title);
            }

            return null;
        }

        /// <summary>
        /// 是否已为视频投币
        /// </summary>
        /// <param name="aid">av号</param>
        /// <returns></returns>
        private bool CanDonatedCoinsForVideo(string aid)
        {
            if (!_alreadyDonatedCoinsCatch.TryGetValue(aid, out int multiply))
            {
                multiply = _dailyTaskApi.GetDonatedCoinsForVideo(aid).Result.Data.Multiply;
                _alreadyDonatedCoinsCatch.TryAdd(aid, multiply);
            }
            if (multiply < 2)
            {
                _logger.LogDebug("已为Av{aid}投过{multiply}枚硬币，可以继续投币", aid, multiply);
                return true;
            }
            else
            {
                _logger.LogDebug("已为Av{aid}投过2枚硬币，不能再投币啦", aid);
                return false;
            }
        }

        #endregion
    }
}

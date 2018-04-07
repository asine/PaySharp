﻿using ICanPay.Alipay.Domain;
using ICanPay.Alipay.Request;
using ICanPay.Alipay.Response;
using ICanPay.Core;
using ICanPay.Core.Exceptions;
using ICanPay.Core.Request;
using ICanPay.Core.Response;
using ICanPay.Core.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ICanPay.Alipay
{
    /// <summary>
    /// 支付宝网关
    /// </summary>
    public sealed class AlipayGateway : BaseGateway
    {
        #region 私有字段

        private readonly Merchant _merchant;

        #endregion

        #region 构造函数

        /// <summary>
        /// 初始化支付宝网关
        /// </summary>
        /// <param name="merchant">商户数据</param>
        public AlipayGateway(Merchant merchant)
            : base(merchant)
        {
            _merchant = merchant;
        }

        #endregion

        #region 属性

        public override string GatewayUrl { get; set; } = "https://openapi.alipay.com";

        public new Notify Notify => (Notify)base.Notify;

        protected override bool IsSuccessPay => Notify.TradeStatus == "TRADE_SUCCESS";

        protected override string[] NotifyVerifyParameter => new string[]
        {
            "app_id","version", "charset","trade_no", "sign","sign_type"
        };

        #endregion

        #region 公共方法

        protected override async Task<bool> ValidateNotifyAsync()
        {
            base.Notify = await GatewayData.ToObjectAsync<Notify>(StringCase.Snake);
            if (IsSuccessResult())
            {
                return true;
            }

            return false;
        }

        protected override string BuildSign(GatewayData gatewayData)
        {
            return EncryptUtil.RSA(gatewayData.ToUrl(false), _merchant.Privatekey, _merchant.SignType);
        }

        protected override bool CheckSign(string data, string sign)
        {
            bool result = EncryptUtil.RSAVerifyData(data, sign, _merchant.AlipayPublicKey, _merchant.SignType);
            if (!result)
            {
                data = data.Replace("/", "\\/");
                result = EncryptUtil.RSAVerifyData(data, sign, _merchant.AlipayPublicKey, _merchant.SignType);
            }

            return result;
        }

        public override TResponse Execute<TModel, TResponse>(Request<TModel, TResponse> request)
        {
            if (request is WapPayRequest || request is WebPayRequest || request is AppPayRequest)
            {
                return SdkExecute(request);
            }
            else if (request is BarcodePayRequest)
            {
                BarcodeExcute(request);
                return default(TResponse);
            }
            else
            {
                return NetExecute(request);
            }
        }

        #endregion

        #region 私有方法

        /// <summary>
        /// 添加商户信息
        /// </summary>
        /// <typeparam name="TModel"></typeparam>
        /// <typeparam name="TResponse"></typeparam>
        /// <param name="request"></param>
        private void AddMerchant<TModel, TResponse>(Request<TModel, TResponse> request) where TResponse : IResponse
        {
            request.RequestUrl = GatewayUrl + request.RequestUrl;
            request.GatewayData.Add(_merchant, StringCase.Snake);
            if (!string.IsNullOrEmpty(request.NotifyUrl))
            {
                request.GatewayData.Add("notify_url", request.NotifyUrl);
            }
            if (!string.IsNullOrEmpty(request.ReturnUrl))
            {
                request.GatewayData.Add("return_url", request.ReturnUrl);
            }
            request.GatewayData.Add(Constant.SIGN, BuildSign(request.GatewayData));
        }

        /// <summary>
        /// 网络执行
        /// </summary>
        /// <typeparam name="TModel"></typeparam>
        /// <typeparam name="TResponse"></typeparam>
        /// <param name="request"></param>
        /// <returns></returns>
        private TResponse NetExecute<TModel, TResponse>(Request<TModel, TResponse> request) where TResponse : IResponse
        {
            AddMerchant(request);

            string result = null;
            Task.Run(async () =>
            {
                result = await HttpUtil
                 .PostAsync(request.RequestUrl, request.GatewayData.ToUrl());
            })
            .GetAwaiter()
            .GetResult();

            var jObject = JObject.Parse(result);
            var jToken = jObject.First.First;
            string sign = jObject.Value<string>("sign");
            if (!CheckSign(jToken.ToString(Formatting.None), sign))
            {
                throw new GatewayException("签名验证失败");
            }

            var baseResponse = (BaseResponse)jToken.ToObject(typeof(TResponse));
            baseResponse.Raw = result;
            baseResponse.Sign = sign;
            return (TResponse)(object)baseResponse;
        }

        /// <summary>
        /// 本地执行
        /// </summary>
        /// <typeparam name="TModel"></typeparam>
        /// <typeparam name="TResponse"></typeparam>
        /// <param name="request"></param>
        /// <returns></returns>
        private TResponse SdkExecute<TModel, TResponse>(Request<TModel, TResponse> request) where TResponse : IResponse
        {
            AddMerchant(request);

            return (TResponse)Activator.CreateInstance(typeof(TResponse), request);
        }

        #region 条码支付
        //TODO:仿照微信公众号
        /// <summary>
        /// 条码执行
        /// </summary>
        /// <typeparam name="TModel"></typeparam>
        /// <typeparam name="TResponse"></typeparam>
        /// <param name="request"></param>
        private void BarcodeExcute<TModel, TResponse>(Request<TModel, TResponse> request) where TResponse : IResponse
        {
            var barcodePayRequest = request as BarcodePayRequest;
            var barcodePayResponse = NetExecute(barcodePayRequest);

            if (barcodePayResponse.Code == "10000")
            {
                barcodePayRequest.OnPaySucceed(barcodePayResponse, null);
                return;
            }

            if (!string.IsNullOrEmpty(barcodePayResponse.TradeNo))
            {
                var queryResponse = new QueryResponse();
                Task.Run(async () =>
                {
                    queryResponse = await PollQueryTradeStateAsync(
                        barcodePayResponse.TradeNo,
                        barcodePayRequest.PollTime,
                        barcodePayRequest.PollCount);
                })
                .GetAwaiter()
                .GetResult();

                if (queryResponse != null)
                {
                    barcodePayRequest.OnPaySucceed(queryResponse, null);
                    return;
                }
                else
                {
                    barcodePayRequest.OnPayFailed(barcodePayResponse, "支付超时");
                    return;
                }
            }

            barcodePayRequest.OnPayFailed(barcodePayResponse, barcodePayResponse.SubMessage);
        }

        /// <summary>
        /// 轮询查询用户是否支付
        /// </summary>
        /// <param name="tradeNo">支付宝订单号</param>
        /// <param name="pollTime">轮询间隔</param>
        /// <param name="pollCount">轮询次数</param>
        /// <returns></returns>
        private QueryResponse PollQueryTradeState(string tradeNo, int pollTime, int pollCount)
        {
            for (int i = 0; i < pollCount; i++)
            {
                Thread.Sleep(pollTime);
                var queryRequest = new QueryRequest();
                queryRequest.AddGatewayData(new QueryModel
                {
                    TradeNo = tradeNo
                });
                var queryResponse = NetExecute(queryRequest);
                if (IsSuccessPay)
                {
                    return queryResponse;
                }
            }

            //支付超时，取消订单
            var cancelRequest = new CancelRequest();
            cancelRequest.AddGatewayData(new CancelModel
            {
                TradeNo = tradeNo
            });
            NetExecute(cancelRequest);

            return null;
        }

        /// <summary>
        /// 轮询查询用户是否支付
        /// </summary>
        /// <param name="tradeNo">支付宝订单号</param>
        /// <param name="pollTime">轮询间隔</param>
        /// <param name="pollCount">轮询次数</param>
        /// <returns></returns>
        private async Task<QueryResponse> PollQueryTradeStateAsync(string tradeNo, int pollTime, int pollCount)
        {
            return await Task.Run(() => PollQueryTradeState(tradeNo, pollTime, pollCount));
        }

        #endregion

        /// <summary>
        /// 是否是已成功支付的支付通知
        /// </summary>
        /// <returns></returns>
        private bool IsSuccessResult()
        {
            if (!ValidateNotifySign())
            {
                throw new GatewayException("签名不一致");
            }

            return true;
        }

        /// <summary>
        /// 验证支付宝通知的签名
        /// </summary>
        private bool ValidateNotifySign()
        {
            GatewayData.Remove("sign");
            GatewayData.Remove("sign_type");

            return EncryptUtil.RSAVerifyData(GatewayData.ToUrl(false),
                Notify.Sign, _merchant.AlipayPublicKey, _merchant.SignType);
        }

        #endregion
    }
}
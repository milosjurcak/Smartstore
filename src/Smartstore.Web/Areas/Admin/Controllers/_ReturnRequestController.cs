﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Linq.Dynamic.Core;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Smartstore.Admin.Models.Orders;
using Smartstore.Core.Catalog.Products;
using Smartstore.Core.Checkout.Orders;
using Smartstore.Core.Common;
using Smartstore.Core.Common.Services;
using Smartstore.Core.Data;
using Smartstore.Core.Localization;
using Smartstore.Core.Rules.Filters;
using Smartstore.Core.Security;
using Smartstore.Core.Stores;
using Smartstore.Web.Controllers;
using Smartstore.Web.Models.DataGrid;
using Smartstore.Web.Rendering;

namespace Smartstore.Admin.Controllers
{
    public class ReturnRequestController : AdminController
    {
        private readonly SmartDbContext _db;
        private readonly ICurrencyService _currencyService;
        private readonly OrderSettings _orderSettings;

        public ReturnRequestController(
            SmartDbContext db,
            ICurrencyService currencyService,
            OrderSettings orderSettings)
        {
            _db = db;
            _currencyService = currencyService;
            _orderSettings = orderSettings;
        }

        public IActionResult Index()
        {
            return RedirectToAction(nameof(List));
        }

        [Permission(Permissions.Order.ReturnRequest.Read)]
        public IActionResult List()
        {
            ViewBag.Stores = Services.StoreContext.GetAllStores().ToSelectListItems();

            return View(new ReturnRequestListModel());
        }

        [Permission(Permissions.Order.ReturnRequest.Read)]
        public async Task<IActionResult> ReturnRequestList(GridCommand command, ReturnRequestListModel model)
        {
            var query = _db.ReturnRequests
                .Include(x => x.Customer.BillingAddress)
                .Include(x => x.Customer.ShippingAddress)
                .AsNoTracking();

            if (model.SearchId.HasValue)
            {
                query = query.Where(x => x.Id == model.SearchId);
            }

            if (model.SearchReturnRequestStatusId.HasValue)
            {
                query = query.Where(x => x.ReturnRequestStatusId == model.SearchReturnRequestStatusId.Value);
            }

            var returnRequests = await query
                .ApplyStandardFilter(null, null, model.SearchStoreId)
                .ApplyGridCommand(command)
                .ToPagedList(command)
                .LoadAsync();

            var orderItemIds = returnRequests.ToDistinctArray(x => x.OrderItemId);
            var orderItems = await _db.OrderItems
                .Include(x => x.Product)
                .Include(x => x.Order)
                .Where(x => orderItemIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x);

            var allStores = Services.StoreContext.GetAllStores().ToDictionary(x => x.Id);

            var rows = await returnRequests.SelectAsync(async x =>
            {
                var m = new ReturnRequestModel();
                await PrepareReturnRequestModel(m, x, orderItems.Get(x.OrderItemId), allStores, false, true);
                return m;
            })
            .AsyncToList();

            return Json(new GridModel<ReturnRequestModel>
            {
                Rows = rows,
                Total = returnRequests.TotalCount
            });
        }

        private async Task PrepareReturnRequestModel(
            ReturnRequestModel model,
            ReturnRequest returnRequest,
            OrderItem orderItem,
            Dictionary<int, Store> allStores,
            bool excludeProperties = false,
            bool forList = false)
        {
            Guard.NotNull(returnRequest, nameof(returnRequest));

            var store = allStores.Get(returnRequest.StoreId);
            var order = orderItem?.Order;

            model.Id = returnRequest.Id;
            model.ProductId = orderItem?.ProductId ?? 0;
            model.ProductSku = orderItem?.Product?.Sku;
            model.ProductName = orderItem?.Product?.Name;
            model.ProductTypeName = orderItem?.Product?.GetProductTypeLabel(Services.Localization);
            model.ProductTypeLabelHint = orderItem?.Product?.ProductTypeLabelHint;
            model.AttributeInfo = orderItem?.AttributeDescription;
            model.OrderId = orderItem?.OrderId ?? 0;
            model.OrderNumber = order?.GetOrderNumber();
            model.CustomerId = returnRequest.CustomerId;
            model.CustomerFullName = returnRequest.Customer.GetFullName().NaIfEmpty();
            model.CanSendEmailToCustomer = returnRequest.Customer.FindEmail().HasValue();
            model.Quantity = returnRequest.Quantity;
            model.ReturnRequestStatusString = await Services.Localization.GetLocalizedEnumAsync(returnRequest.ReturnRequestStatus);
            model.CreatedOn = Services.DateTimeHelper.ConvertToUserTime(returnRequest.CreatedOnUtc, DateTimeKind.Utc);
            model.UpdatedOn = Services.DateTimeHelper.ConvertToUserTime(returnRequest.UpdatedOnUtc, DateTimeKind.Utc);
            model.EditUrl = Url.Action("Edit", "ReturnRequest", new { id = returnRequest.Id });
            model.CustomerEditUrl = Url.Action("Edit", "Customer", new { id = returnRequest.CustomerId });

            if (orderItem != null)
            {
                model.OrderEditUrl = Url.Action("Edit", "Order", new { id = orderItem.OrderId });
                model.ProductEditUrl = Url.Action("Edit", "Product", new { id = orderItem.ProductId });
            }

            if (allStores.Count > 1)
            {
                model.StoreName = store?.Name;
            }

            if (!excludeProperties)
            {
                model.ReasonForReturn = returnRequest.ReasonForReturn;
                model.RequestedAction = returnRequest.RequestedAction;

                if (returnRequest.RequestedActionUpdatedOnUtc.HasValue)
                {
                    model.RequestedActionUpdated = Services.DateTimeHelper.ConvertToUserTime(returnRequest.RequestedActionUpdatedOnUtc.Value, DateTimeKind.Utc);
                }

                model.CustomerComments = returnRequest.CustomerComments;
                model.StaffNotes = returnRequest.StaffNotes;
                model.AdminComment = returnRequest.AdminComment;
                model.ReturnRequestStatusId = returnRequest.ReturnRequestStatusId;
            }

            if (!forList)
            {
                string returnRequestReasons = _orderSettings.GetLocalizedSetting(x => x.ReturnRequestReasons, order?.CustomerLanguageId, store?.Id, true, false);
                string returnRequestActions = _orderSettings.GetLocalizedSetting(x => x.ReturnRequestActions, order?.CustomerLanguageId, store?.Id, true, false);
                string unspec = T("Common.Unspecified");

                var reasonForReturn = returnRequestReasons.SplitSafe(',')
                    .Select(x => new SelectListItem { Text = x, Value = x, Selected = x == returnRequest.ReasonForReturn })
                    .ToList();
                reasonForReturn.Insert(0, new SelectListItem { Text = unspec, Value = string.Empty });

                var actionsForReturn = returnRequestActions.SplitSafe(',')
                    .Select(x => new SelectListItem { Text = x, Value = x, Selected = x == returnRequest.RequestedAction })
                    .ToList();
                actionsForReturn.Insert(0, new SelectListItem { Text = unspec, Value = string.Empty });

                ViewBag.ReasonForReturn = reasonForReturn;
                ViewBag.ActionsForReturn = actionsForReturn;

                model.UpdateOrderItem.Id = returnRequest.Id;
                model.UpdateOrderItem.Caption = T("Admin.ReturnRequests.Accept.Caption");
                model.UpdateOrderItem.PostUrl = Url.Action("Accept", "ReturnRequest");
                model.UpdateOrderItem.UpdateTotals = model.UpdateOrderItem.ShowUpdateTotals;

                if (order != null)
                {
                    model.UpdateOrderItem.UpdateRewardPoints = order.RewardPointsWereAdded;
                    model.UpdateOrderItem.ShowUpdateTotals = order.OrderStatusId <= (int)OrderStatus.Pending;
                    model.UpdateOrderItem.ShowUpdateRewardPoints = order.OrderStatusId > (int)OrderStatus.Pending && order.RewardPointsWereAdded;
                }

                model.ReturnRequestInfo = TempData[UpdateOrderDetailsContext.InfoKey] as string;

                // The maximum amount that can be refunded for this return request.
                if (orderItem != null)
                {
                    var maxRefundAmount = Math.Max(orderItem.UnitPriceInclTax * returnRequest.Quantity, 0);
                    if (maxRefundAmount > decimal.Zero)
                    {
                        model.MaxRefundAmount = new Money(maxRefundAmount, _currencyService.PrimaryCurrency, false, _currencyService.GetTaxFormat(true, true));
                    }
                }
            }
        }
    }
}
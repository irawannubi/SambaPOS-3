﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Linq;
using Samba.Domain.Models.Accounts;
using Samba.Domain.Models.Entities;
using Samba.Domain.Models.Settings;
using Samba.Domain.Models.Tickets;
using Samba.Infrastructure.Data.Serializer;
using Samba.Localization.Properties;
using Samba.Persistance;
using Samba.Persistance.Common;
using Samba.Presentation.Services.Common;
using Samba.Services;
using Samba.Services.Common;

namespace Samba.Presentation.Services.Implementations.TicketModule
{
    [Export(typeof(ITicketService))]
    public class TicketService : ITicketService
    {
        private readonly ITicketDao _ticketDao;
        private readonly ITicketServiceBase _ticketServiceBase;
        private readonly IApplicationState _applicationState;
        private readonly IExpressionService _expressionService;
        private readonly IUserService _userService;
        private readonly ISettingService _settingService;
        private readonly IAccountService _accountService;
        private readonly ICacheService _cacheService;

        [ImportingConstructor]
        public TicketService(ITicketDao ticketDao, ITicketServiceBase ticketServiceBase, IDepartmentService departmentService, IApplicationState applicationState,
            IUserService userService, ISettingService settingService, IExpressionService expressionService, IAccountService accountService, ICacheService cacheService)
        {
            _ticketDao = ticketDao;
            _ticketServiceBase = ticketServiceBase;
            _expressionService = expressionService;
            _applicationState = applicationState;
            _userService = userService;
            _settingService = settingService;
            _accountService = accountService;
            _cacheService = cacheService;
        }

        private decimal GetExchangeRate(Account account)
        {
            if (account.ForeignCurrencyId == 0) return 1;
            return _cacheService.GetCurrencyById(account.ForeignCurrencyId).ExchangeRate;
        }

        public void UpdateEntity(Ticket ticket, int entityTypeId, int entityId, string entityName, int accountTypeId, int accountId, string entityCustomData)
        {
            var currentEntity = ticket.TicketEntities.SingleOrDefault(x => x.EntityTypeId == entityTypeId);
            var currentEntityId = currentEntity != null ? currentEntity.EntityId : 0;
            var newEntityName = entityName;
            var oldEntityName = currentEntity != null ? currentEntity.EntityName : "";

            if (currentEntity != null && currentEntity.EntityId != entityId)
            {
                var entityType = _cacheService.GetEntityTypeById(currentEntity.EntityTypeId);
                _applicationState.NotifyEvent(RuleEventNames.EntityUpdated, new
                {
                    EntityTypeId = currentEntity.EntityTypeId,
                    EntityId = currentEntity.EntityId,
                    EntityTypeName = entityType.Name,
                    OpenTicketCount = GetOpenTicketCount(currentEntity.EntityId, ticket.Id)
                });
            }

            ticket.UpdateEntity(entityTypeId, entityId, entityName, accountTypeId, accountId, entityCustomData);

            if (currentEntityId != entityId)
            {
                var entityType = _cacheService.GetEntityTypeById(entityTypeId);
                _applicationState.NotifyEvent(RuleEventNames.TicketEntityChanged,
                    new
                    {
                        Ticket = ticket,
                        EntityTypeId = entityTypeId,
                        EntityId = entityId,
                        EntityTypeName = entityType.Name,
                        OldEntityName = oldEntityName,
                        NewEntityName = newEntityName,
                        OrderCount = ticket.Orders.Count,
                        CustomData = entityCustomData
                    });
            }
        }

        private int GetOpenTicketCount(int entityId, int ticketId)
        {
            var ids = _ticketServiceBase.GetOpenTicketIds(entityId).ToList();
            if (ticketId > 0 && !ids.Contains(ticketId)) ids.Add(ticketId);
            return ids.Count - (ticketId > 0 ? 1 : 0);
        }

        public void UpdateEntity(Ticket ticket, Entity entity)
        {
            if (entity == null) return;
            var entityType = _cacheService.GetEntityTypeById(entity.EntityTypeId);
            UpdateEntity(ticket, entityType.Id, entity.Id, entity.Name, entityType.AccountTypeId, entity.AccountId, entity.CustomData);
        }

        public Ticket OpenTicket(int ticketId)
        {
            Debug.Assert(_applicationState.CurrentDepartment != null);

            var ticket = ticketId == 0
                             ? CreateTicket()
                             : _ticketDao.OpenTicket(ticketId);

            _applicationState.NotifyEvent(RuleEventNames.TicketOpened, new { Ticket = ticket, OrderCount = ticket.Orders.Count });

            return ticket;
        }

        private Ticket CreateTicket()
        {
            var account = _cacheService.GetAccountById(_applicationState.CurrentTicketType.SaleTransactionType.DefaultTargetAccountId);
            var result = Ticket.Create(
                _applicationState.CurrentDepartment.Model,
                _applicationState.CurrentTicketType,
                GetExchangeRate(account),
                _applicationState.GetCalculationSelectors().Where(x => string.IsNullOrEmpty(x.ButtonHeader)).SelectMany(y => y.CalculationTypes));
            _applicationState.NotifyEvent(RuleEventNames.TicketCreated, new { Ticket = result, TicketTypeName = _applicationState.CurrentTicketType.Name });
            return result;
        }

        public TicketCommitResult CloseTicket(Ticket ticket)
        {
            var result = _ticketDao.CheckConcurrency(ticket);
            Debug.Assert(ticket != null);
            var changed = !string.IsNullOrEmpty(result.ErrorMessage);
            var canSumbitTicket = !changed && ticket.CanSubmit;

            if (canSumbitTicket)
            {
                RecalculateTicket(ticket);
                _applicationState.NotifyEvent(RuleEventNames.BeforeTicketClosing, new { Ticket = ticket, TicketId = ticket.Id, ticket.RemainingAmount, ticket.TotalAmount });
                if (ticket.Orders.Count > 0)
                {
                    var ticketType = _cacheService.GetTicketTypeById(ticket.TicketTypeId);

                    if (ticket.Orders.FirstOrDefault(x => !x.Locked) != null)
                    {
                        var number = _settingService.GetNextNumber(ticketType.OrderNumerator.Id);
                        ticket.MergeOrdersAndUpdateOrderNumbers(number);
                    }

                    if (ticket.Id == 0)
                    {
                        UpdateTicketNumber(ticket, ticketType.TicketNumerator);
                        _ticketDao.Save(ticket);
                    }

                    Debug.Assert(!string.IsNullOrEmpty(ticket.TicketNumber));
                    Debug.Assert(ticket.Id > 0);
                    _applicationState.NotifyEvent(RuleEventNames.TicketClosing, new { Ticket = ticket, TicketId = ticket.Id, ticket.RemainingAmount, ticket.TotalAmount });
                    ticket.LockTicket();
                }

                ticket.RemoveZeroAmountAccountTransactions();

                if (ticket.Id > 0)// eğer adisyonda satır yoksa ID burada 0 olmalı.
                    _ticketDao.Save(ticket);

                Debug.Assert(ticket.Orders.Count(x => x.OrderNumber == 0) == 0);
            }

            if (ticket.Id > 0)
            {
                foreach (var ticketEntity in ticket.TicketEntities)
                {
                    var entityType = _cacheService.GetEntityTypeById(ticketEntity.EntityTypeId);
                    _applicationState.NotifyEvent(RuleEventNames.EntityUpdated, new
                                                                                       {
                                                                                           EntityTypeId = ticketEntity.EntityTypeId,
                                                                                           EntityId = ticketEntity.EntityId,
                                                                                           EntityTypeName = entityType.Name,
                                                                                           OpenTicketCount = _ticketServiceBase.GetOpenTicketIds(ticketEntity.EntityId).Count()
                                                                                       });
                }
            }

            result.TicketId = ticket.Id;
            return result;
        }

        public void AddPayment(Ticket ticket, PaymentType paymentType, Account account, decimal amount, decimal tenderedAmount)
        {
            if (account == null) return;
            var remainingAmount = ticket.GetRemainingAmount();
            var changeAmount = tenderedAmount > remainingAmount ? tenderedAmount - remainingAmount : 0;
            ticket.AddPayment(paymentType, account, amount, GetExchangeRate(account), _applicationState.CurrentLoggedInUser.Id);
            _applicationState.NotifyEvent(RuleEventNames.PaymentProcessed,
                new
                {
                    Ticket = ticket,
                    PaymentTypeName = paymentType.Name,
                    TenderedAmount = tenderedAmount,
                    ProcessedAmount = tenderedAmount - changeAmount,
                    ChangeAmount = changeAmount,
                    SelectedQuantity = ticket.PaidItems.Sum(x => x.Quantity),
                    RemainingAmount = ticket.GetRemainingAmount()
                });
        }

        public void AddChangePayment(Ticket ticket, ChangePaymentType paymentType, Account account, decimal amount)
        {
            if (account == null) return;
            ticket.AddChangePayment(paymentType, account, amount, GetExchangeRate(account), _applicationState.CurrentLoggedInUser.Id);
        }

        public void PayTicket(Ticket ticket, PaymentType template)
        {
            var amount = ticket.GetRemainingAmount();
            AddPayment(ticket, template, template.Account ?? GetAccountForPayment(ticket, template), amount, amount);
        }

        public Account GetAccountForPayment(Ticket ticket, PaymentType paymentType)
        {
            var rt = _cacheService.GetEntityTypes().Where(
                x => x.AccountTypeId == paymentType.AccountTransactionType.TargetAccountTypeId).Select(x => x.Id);
            var tr = ticket.TicketEntities.FirstOrDefault(x => rt.Contains(x.EntityTypeId));
            return tr != null ? _accountService.GetAccountById(tr.AccountId) : null;
        }

        public void UpdateTicketNumber(Ticket ticket, Numerator numerator)
        {
            if (string.IsNullOrEmpty(ticket.TicketNumber))
            {
                ticket.TicketNumber = _settingService.GetNextString(numerator.Id);
            }
        }

        public TicketCommitResult MergeTickets(IEnumerable<int> ticketIds)
        {
            var ticketList = ticketIds.Select(OpenTicket).ToList();

            if (ticketList.Any(x => x.Calculations.Any()))
                return new TicketCommitResult { ErrorMessage = string.Format("Can't merge tickets\r{0}", "contains calculations") };

            var entitiesUnMatches = ticketList.SelectMany(x => x.TicketEntities).GroupBy(x => x.EntityTypeId).Any(x => x.Select(y => y.EntityId).Distinct().Count() > 1);
            if (entitiesUnMatches) return new TicketCommitResult { ErrorMessage = string.Format("Can't merge tickets\r{0}", "Entities doesn't match") };

            var clonedOrders = ticketList.SelectMany(x => x.Orders).Select(ObjectCloner.Clone).ToList();
            var clonedPayments = ticketList.SelectMany(x => x.Payments).Select(ObjectCloner.Clone2).ToList();
            var clonedChangePayments = ticketList.SelectMany(x => x.ChangePayments).Select(ObjectCloner.Clone2).ToList();
            var clonedTags = ticketList.SelectMany(x => x.GetTicketTagValues()).Select(ObjectCloner.Clone).ToList();
            var clonedEntites = ticketList.SelectMany(x => x.TicketEntities).Select(ObjectCloner.Clone).ToList();

            ticketList.ForEach(x => x.RemoveData());
            ticketList.ForEach(x => CloseTicket(x));

            var ticket = CreateTicket();

            clonedOrders.ForEach(ticket.Orders.Add);

            foreach (var cp in clonedPayments)
            {
                var account = _accountService.GetAccountById(cp.AccountTransaction.TargetTransactionValue.AccountId);
                ticket.AddPayment(_cacheService.GetPaymentTypeById(cp.PaymentTypeId), account, cp.Amount, GetExchangeRate(account), cp.UserId);
            }

            foreach (var cp in clonedChangePayments)
            {
                var account = _accountService.GetAccountById(cp.AccountTransaction.TargetTransactionValue.AccountId);
                ticket.AddChangePayment(_cacheService.GetChangePaymentTypeById(cp.ChangePaymentTypeId), account, cp.Amount, GetExchangeRate(account), cp.UserId);
            }

            clonedEntites.ForEach(x => ticket.UpdateEntity(x.EntityTypeId, x.EntityId, x.EntityName, x.AccountTypeId, x.AccountId, x.EntityCustomData));
            clonedTags.ForEach(x => ticket.SetTagValue(x.TagName, x.TagValue));

            RefreshAccountTransactions(ticket);

            _applicationState.NotifyEvent(RuleEventNames.TicketsMerged, new { Ticket = ticket });
            return CloseTicket(ticket);
        }

        public TicketCommitResult MoveOrders(Ticket ticket, Order[] selectedOrders, int targetTicketId)
        {
            _applicationState.NotifyEvent(RuleEventNames.TicketMoving, new { Ticket = ticket });

            var clonedOrders = selectedOrders.Select(ObjectCloner.Clone2).ToList();
            ticket.RemoveOrders(selectedOrders);

            CloseTicket(ticket);
            ticket = OpenTicket(targetTicketId);

            foreach (var clonedOrder in clonedOrders)
            {
                clonedOrder.TicketId = 0;
                ticket.Orders.Add(clonedOrder);
                _applicationState.NotifyEvent(RuleEventNames.OrderMoved, new { Ticket = ticket, Order = clonedOrder, clonedOrder.MenuItemName });
            }

            RefreshAccountTransactions(ticket);
            ticket.LastOrderDate = DateTime.Now;

            _applicationState.NotifyEvent(RuleEventNames.TicketMoved, new { Ticket = ticket });

            return CloseTicket(ticket);
        }

        public void RecalculateTicket(Ticket ticket)
        {
            var total = ticket.TotalAmount;
            ticket.Calculations.Where(x => x.CalculationType == 5).ToList().ForEach(
                x => x.Amount = _expressionService.EvalCommand(FunctionNames.Calculation, "_" + x.Name, new { Ticket = ticket }, 0m));
            ticket.Recalculate();
            if (total != ticket.TotalAmount)
            {
                _applicationState.NotifyEvent(RuleEventNames.TicketTotalChanged,
                    new
                    {
                        Ticket = ticket,
                        PreviousTotal = total,
                        TicketTotal = ticket.GetSum(),
                        DiscountTotal = ticket.GetPreTaxServicesTotal(),
                        PaymentTotal = ticket.GetPaymentAmount(),
                        RemainingAmount = ticket.GetRemainingAmount()
                    });
            }
        }

        public void UpdateTicketState(Ticket ticket, string stateName, string currentState, string state, string stateValue, int quantity = 0)
        {
            var sv = ticket.GetStateValue(stateName);
            if (!string.IsNullOrEmpty(currentState) && sv.State != currentState) return;
            if (sv != null && sv.StateName == stateName && sv.StateValue == stateValue && sv.Quantity == quantity && sv.State == state) return;

            ticket.SetStateValue(stateName, state, stateValue, quantity);

            _applicationState.NotifyEvent(RuleEventNames.TicketStateUpdated,
            new
            {
                Ticket = ticket,
                StateName = stateName,
                State = state,
                StateValue = stateValue,
                Quantity = quantity,
                TicketState = ticket.GetStateData()
            });
        }

        public void UpdateTag(Ticket ticket, TicketTagGroup tagGroup, TicketTag ticketTag)
        {
            ticket.SetTagValue(tagGroup.Name, ticketTag.Name);

            if (tagGroup.FreeTagging && tagGroup.SaveFreeTags)
            {
                SaveFreeTicketTag(tagGroup.Id, ticketTag.Name);
            }

            var tagData = new TicketTagData
            {
                Ticket = ticket,
                TicketTagGroup = tagGroup,
                TagName = tagGroup.Name,
                TagValue = ticketTag.Name
            };

            _applicationState.NotifyEvent(RuleEventNames.TicketTagSelected,
                        new
                        {
                            Ticket = ticket,
                            tagData.TagName,
                            tagData.TagValue,
                            NumericValue = tagGroup.IsNumeric ? Convert.ToDecimal(ticketTag.Name) : 0,
                            TicketTag = ticket.GetTagData()
                        });
        }

        public void SaveFreeTicketTag(int tagGroupId, string freeTag)
        {
            _ticketDao.SaveFreeTicketTag(tagGroupId, freeTag);
            _cacheService.ResetTicketTagCache();
        }

        public void TagOrders(Ticket ticket, IEnumerable<Order> selectedOrders, OrderTagGroup orderTagGroup, OrderTag orderTag, string tagNote)
        {
            var so = selectedOrders.ToList();

            if (orderTagGroup.MaxSelectedItems == 1)
            {
                foreach (var order in so.Where(x => x.OrderTagExists(y => y.OrderTagGroupId == orderTagGroup.Id && y.TagValue != orderTag.Name)))
                {
                    var orderTagValue = order.GetOrderTagValues().First(x => x.OrderTagGroupId == orderTagGroup.Id);
                    order.UntagOrder(orderTagValue);
                    _applicationState.NotifyEvent(RuleEventNames.OrderUntagged,
                               new
                               {
                                   Ticket = ticket,
                                   Order = order,
                                   OrderTagName = orderTagGroup.Name,
                                   OrderTagValue = orderTagValue.TagValue
                               });

                }
            }

            foreach (var selectedOrder in so)
            {
                var result = selectedOrder.ToggleOrderTag(orderTagGroup, orderTag, _applicationState.CurrentLoggedInUser.Id, tagNote);

                if (orderTagGroup.SaveFreeTags && orderTagGroup.OrderTags.All(x => x.Name != orderTag.Name))
                {
                    _ticketDao.SaveFreeOrderTag(orderTagGroup.Id, orderTag);
                    _cacheService.ResetOrderTagCache();
                }
                _applicationState.NotifyEvent(result ? RuleEventNames.OrderTagged : RuleEventNames.OrderUntagged,
                new
                {
                    Ticket = ticket,
                    Order = selectedOrder,
                    OrderTagName = orderTagGroup.Name,
                    OrderTagValue = orderTag.Name
                });
            }
        }

        public void UntagOrders(Ticket ticket, IEnumerable<Order> selectedOrders, OrderTagGroup orderTagGroup, OrderTag orderTag)
        {
            foreach (var selectedOrder in selectedOrders.Where(selectedOrder => selectedOrder.UntagIfTagged(orderTagGroup, orderTag)))
            {
                _applicationState.NotifyEvent(RuleEventNames.OrderUntagged,
                                               new
                                                   {
                                                       Ticket = ticket,
                                                       Order = selectedOrder,
                                                       OrderTagName = orderTagGroup.Name,
                                                       OrderTagValue = orderTag.Name
                                                   });
            }
        }

        public bool CanDeselectOrders(IEnumerable<Order> selectedOrders)
        {
            return selectedOrders.All(CanDeselectOrder);
        }

        public bool CanDeselectOrder(Order order)
        {
            if (!order.DecreaseInventory || order.Locked) return true;
            var ots = _applicationState.GetOrderTagGroups(order.MenuItemId);
            return ots.Where(x => x.MinSelectedItems > 0).All(orderTagGroup => order.GetOrderTagValues(x => x.OrderTagGroupId == orderTagGroup.Id).Count() >= orderTagGroup.MinSelectedItems);
        }

        public OrderTagGroup GetMandantoryOrderTagGroup(Order order)
        {
            if (order.Locked) return null;
            var ots = _applicationState.GetOrderTagGroups(order.MenuItemId);
            return ots.Where(x => x.MinSelectedItems > 0).FirstOrDefault(orderTagGroup => order.GetOrderTagValues(x => x.OrderTagGroupId == orderTagGroup.Id).Count() < orderTagGroup.MinSelectedItems);
        }

        public bool CanCloseTicket(Ticket ticket)
        {
            if (!ticket.IsLocked)
                return CanDeselectOrders(ticket.Orders);
            return true;
        }

        public void RefreshAccountTransactions(Ticket ticket)
        {
            foreach (var template in from order in ticket.Orders.GroupBy(x => x.AccountTransactionTypeId)
                                     where ticket.TransactionDocument.AccountTransactions.All(x => x.AccountTransactionTypeId != order.Key)
                                     select _cacheService.GetAccountTransactionTypeById(order.Key))
            {
                var transaction = ticket.TransactionDocument.AddNewTransaction(template, ticket.GetTicketAccounts());
                transaction.Reversable = false;
            }

            foreach (var taxTransactionTemplate in ticket.GetTaxIds().Select(x => _cacheService.GetAccountTransactionTypeById(x)))
            {
                ticket.TransactionDocument.AddSingletonTransaction(taxTransactionTemplate.Id,
                       taxTransactionTemplate, ticket.GetTicketAccounts());
            }
        }

        public void UpdateOrderStates(Ticket ticket, IList<Order> orders, string stateName, string currentState, int groupOrder, string state, int stateOrder,
                                       string stateValue)
        {
            var so = orders.Where(x => string.IsNullOrEmpty(currentState) || x.IsInState(stateName, currentState)).ToList();
            foreach (var order in so)
            {
                if (order.IsInState(stateName, state) && (string.IsNullOrEmpty(stateValue) || order.IsInState(stateValue))) continue;
                order.SetStateValue(stateName, groupOrder, state, stateOrder, stateValue);
                _applicationState.NotifyEvent(RuleEventNames.OrderStateUpdated,
                                               new
                                                   {
                                                       Ticket = ticket,
                                                       Order = order,
                                                       StateName = stateName,
                                                       State = state,
                                                       StateValue = stateValue,
                                                   });
            }
        }

        public void ChangeOrdersAccountTransactionTypeId(Ticket ticket, IEnumerable<Order> selectedOrders, int accountTransactionTypeId)
        {
            var so = selectedOrders.ToList();
            var accountTransactionTypeIds = so.GroupBy(x => x.AccountTransactionTypeId).Select(x => x.Key).ToList();
            so.ForEach(x => x.AccountTransactionTypeId = accountTransactionTypeId);
            accountTransactionTypeIds.Where(x => so.All(y => y.AccountTransactionTypeId != x)).ToList()
                .ForEach(x => ticket.TransactionDocument.AccountTransactions.Where(y => y.AccountTransactionTypeId == x).ToList().ForEach(y => ticket.TransactionDocument.AccountTransactions.Remove(y)));
            RefreshAccountTransactions(ticket);
        }

        public void AddAccountTransaction(Ticket ticket, Account sourceAccount, Account targetAccount, decimal amount, decimal exchangeRate)
        {
            var transactionType = _cacheService.FindAccountTransactionType(sourceAccount.AccountTypeId, targetAccount.AccountTypeId, sourceAccount.Id, targetAccount.Id);
            if (transactionType != null)
            {
                ticket.TransactionDocument.AddNewTransaction(transactionType, ticket.GetTicketAccounts(), amount, exchangeRate);
            }
        }

        public bool CanMakeAccountTransaction(TicketEntity ticketEntity, AccountTransactionType accountTransactionType, decimal targetBalance)
        {
            if (ticketEntity.AccountId == 0) return false;
            var entityType = _cacheService.GetEntityTypeById(ticketEntity.EntityTypeId);
            var typeId = accountTransactionType.TargetAccountTypeId;
            if (accountTransactionType.DefaultSourceAccountId == 0)
                typeId = accountTransactionType.SourceAccountTypeId;
            var result = entityType.AccountTypeId == typeId;
            if (result)
            {
                var accountType = _cacheService.GetAccountTypeById(entityType.AccountTypeId);
                if (accountType.WorkingRule != 0)
                {
                    if (accountType.WorkingRule == 1 && targetBalance < 0) return false; //disallow credit
                    if (accountType.WorkingRule == 2 && targetBalance > ticketEntity.GetCustomDataAsDecimal(Resources.CreditLimit)) return false; //disallow debit
                }
            }
            return result;
        }

        public void UpdateOrderPrice(Order order, string portionName, string priceTag)
        {
            var mi = _cacheService.GetMenuItem(x => x.Id == order.MenuItemId);
            if (mi != null)
            {
                var portion = !string.IsNullOrEmpty(portionName)
                                  ? mi.Portions.FirstOrDefault(x => x.Name == portionName)
                                  : mi.Portions.First();
                order.UpdatePortion(portion, priceTag, null);
            }
        }

        public Order AddOrder(Ticket ticket, int menuItemId, decimal quantity, string portionName, OrderTagTemplate template)
        {
            if (ticket.IsLocked && !_userService.IsUserPermittedFor(PermissionNames.AddItemsToLockedTickets)) return null;
            if (!ticket.CanSubmit) return null;
            var menuItem = _cacheService.GetMenuItem(x => x.Id == menuItemId);
            var portion = _cacheService.GetMenuItemPortion(menuItemId, portionName);
            if (portion == null) return null;
            var priceTag = _applicationState.CurrentDepartment.PriceTag;
            var productTimer = _applicationState.GetProductTimer(menuItemId);
            var order = ticket.AddOrder(
                _applicationState.CurrentTicketType.SaleTransactionType,
                _applicationState.CurrentDepartment.Model,
                _applicationState.CurrentLoggedInUser.Name, menuItem,
                _applicationState.GetTaxTemplates(menuItem.Id).ToList(), portion, priceTag, productTimer);

            order.Quantity = quantity > 9 ? decimal.Round(quantity / portion.Multiplier, 3, MidpointRounding.AwayFromZero) : quantity;

            if (template != null) template.OrderTagTemplateValues.ToList().ForEach(x => order.ToggleOrderTag(x.OrderTagGroup, x.OrderTag, 0, ""));
            RecalculateTicket(ticket);
            _applicationState.NotifyEvent(RuleEventNames.OrderAdded, new { Ticket = ticket, Order = order, MenuItemName = order.MenuItemName, MenuItemTag = menuItem.Tag, MenuItemGroupCode = menuItem.GroupCode });

            return order;
        }
    }
}

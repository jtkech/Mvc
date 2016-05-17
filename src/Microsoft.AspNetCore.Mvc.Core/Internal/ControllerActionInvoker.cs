// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
#if NETSTANDARD1_5
using System.Reflection;
#endif
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Core;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Mvc.Internal
{
    public class ControllerActionInvoker : IActionInvoker
    {
        private readonly IControllerFactory _controllerFactory;
        private readonly IControllerArgumentBinder _controllerArgumentBinder;
        private readonly DiagnosticSource _diagnosticSource;
        private readonly ILogger _logger;

        private readonly ControllerContext _controllerContext;
        private readonly IFilterMetadata[] _filters;
        private readonly ObjectMethodExecutor _executor;
        private readonly Func<ControllerContext, object> _createController;
        private readonly Action<ControllerContext, object> _releaseController;

        // Do not make this readonly, it's mutable. We don't want to make a copy.
        // https://blogs.msdn.microsoft.com/ericlippert/2008/05/14/mutating-readonly-structs/
        private FilterCursor _cursor;
        private Dictionary<string, object> _arguments;
        private object _controller;
        private IActionResult _result;
        private Exception _exception;
        private ExceptionDispatchInfo _exceptionDispatchInfo;
        private bool _exceptionHandled;
        private IActionResult _exceptionResult;

        private AuthorizationFilterContext _authorizationContext;

        private ResourceExecutingContext _resourceExecutingContext;
        private ResourceExecutedContext _resourceExecutedContext;

        private ExceptionContext _exceptionContext;

        private ActionExecutingContext _actionExecutingContext;
        private ActionExecutedContext _actionExecutedContext;

        private ResultExecutingContext _resultExecutingContext;
        private ResultExecutedContext _resultExecutedContext;

        public ControllerActionInvoker(
            ControllerActionInvokerCache cache,
            IControllerFactory controllerFactory,
            IControllerArgumentBinder controllerArgumentBinder,
            ILogger logger,
            DiagnosticSource diagnosticSource,
            ActionContext actionContext,
            IReadOnlyList<IValueProviderFactory> valueProviderFactories,
            int maxModelValidationErrors)
        {
            if (cache == null)
            {
                throw new ArgumentNullException(nameof(cache));
            }

            if (controllerFactory == null)
            {
                throw new ArgumentNullException(nameof(controllerFactory));
            }

            if (controllerArgumentBinder == null)
            {
                throw new ArgumentNullException(nameof(controllerArgumentBinder));
            }

            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            if (diagnosticSource == null)
            {
                throw new ArgumentNullException(nameof(diagnosticSource));
            }

            if (actionContext == null)
            {
                throw new ArgumentNullException(nameof(actionContext));
            }

            if (valueProviderFactories == null)
            {
                throw new ArgumentNullException(nameof(valueProviderFactories));
            }

            _controllerFactory = controllerFactory;
            _controllerArgumentBinder = controllerArgumentBinder;
            _logger = logger;
            _diagnosticSource = diagnosticSource;

            _controllerContext = new ControllerContext(actionContext);
            _controllerContext.ModelState.MaxAllowedErrors = maxModelValidationErrors;

            // PERF: These are rarely going to be changed, so let's go copy-on-write.
            _controllerContext.ValueProviderFactories = new CopyOnWriteList<IValueProviderFactory>(valueProviderFactories);

            var cacheEntry = cache.GetState(_controllerContext);
            _filters = cacheEntry.Filters;
            _executor = cacheEntry.ActionMethodExecutor;
            _cursor = new FilterCursor(_filters);
            _createController = cacheEntry.CreateController;
            _releaseController = cacheEntry.ReleaseController;
        }

        public virtual async Task InvokeAsync()
        {
            object controller = null;
            IActionResult result = null;
            var controllerContext = _controllerContext;
            var diagnosticSource = _diagnosticSource;

            _cursor.Reset();
            await InvokeNextAuthorizationFilterAsync();

            // If Authorization Filters return a result, it's a short circuit because
            // authorization failed. We don't execute Result Filters around the result.
            result = _result;
            if (_result != null)
            {
                diagnosticSource.BeforeActionResult(controllerContext, result);

                try
                {
                    await result.ExecuteResultAsync(controllerContext);
                }
                finally
                {
                    diagnosticSource.AfterActionResult(controllerContext, result);
                }

                return;
            }

            try
            {
                _cursor.Reset();
                await InvokeNextResourceFilterAsync();

                if (_resourceExecutedContext != null)
                {
                    // This means we executed resource filters. We only need to handle unhandled exceptions, because
                    // if resource filters ran there's nothing else do it.
                    HandleException();
                    Rethrow();

                    return;
                }

                // If we get here then we didn't have any resource filters, so let's run the exception filters.
                _cursor.Reset();
                await InvokeNextExceptionFilterAsync();

                if (_exceptionContext != null)
                {
                    // This means we executed exception filters. We need to handle a short circuit result as well as
                    // any unhandled exceptions. Either of these cases will end the pipeline.
                    var exceptionResult = _exceptionResult;
                    if (exceptionResult != null)
                    {
                        _exception = null;
                        _exceptionDispatchInfo = null;

                        result = exceptionResult;
                        _result = exceptionResult;
                        _exceptionResult = null;

                        // If Exception Filters provide a result, it's a short-circuit to 'handle' an exception.
                        // We don't execute Result Filters around the result since it's not a 'normal' result.
                        diagnosticSource.BeforeActionResult(controllerContext, result);

                        try
                        {
                            await result.ExecuteResultAsync(controllerContext);
                        }
                        finally
                        {
                            diagnosticSource.AfterActionResult(controllerContext, result);
                        }

                        return;
                    }

                    // We need to rethrow any unhandled exceptions, since an unhandled exception in an
                    // exception filter would prevent the result from running.
                    Rethrow();

                    // We don't need to look at the outcome of executing action filters or the action here. That would
                    // be taken care of inside of InvokeActionFiltersInsideExceptionFilter.
                }
                else
                {
                    // If we get here then this means that we didn't have any exception filters. We need to 
                    // run action filters and possibly the action itself.
                    controller = _createController(controllerContext);
                    _controller = controller;

                    var arguments = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    _arguments = arguments;
                    await _controllerArgumentBinder.BindArgumentsAsync(controllerContext, controller, arguments);

                    _cursor.Reset();
                    await InvokeNextActionFilterAsync();

                    if (_actionExecutedContext != null)
                    {
                        // We need to rethrow any unhandled exceptions, since an unhandled exception in an
                        // action filter would prevent the result from running.
                        HandleException();
                        Rethrow();
                    }
                    else
                    {
                        // If we get here then this means that we didn't have any action filters. We need to 
                        // run the action itself.
                        try
                        {
                            diagnosticSource.BeforeActionMethod(controllerContext, arguments, controller);

                            var method = controllerContext.ActionDescriptor.MethodInfo;

                            var orderedArguments = ControllerActionExecutor.PrepareArguments(arguments, method.GetParameters());

                            _logger.ActionMethodExecuting(_actionExecutingContext, orderedArguments);

                            var actionReturnValue = await ControllerActionExecutor.ExecuteAsync(
                                _executor,
                                controller,
                                arguments);

                            result = CreateActionResult(method.ReturnType, actionReturnValue);
                            _result = result;

                            _logger.ActionMethodExecuted(_actionExecutingContext, result);
                        }
                        finally
                        {
                            diagnosticSource.AfterActionMethod(controllerContext, arguments, controller, result);
                        }
                    }
                }

                // If we didn't short-circuit before now then we have a 'success' result and should execute
                // resource filters around it.
                _cursor.Reset();
                await InvokeNextResultFilterAsync();

                if (_resultExecutedContext != null)
                {
                    // This means we executed result filters. We need to rethrow any unhandled exceptions so
                    // they will be visible outside of MVC.
                    HandleException();
                    Rethrow();

                    return;
                }
                else
                {
                    // If we get here then this means that we didn't have any result filters. We need to 
                    // run the result itself.
                    result = _result;
                    if (result == null)
                    {
                        result = new EmptyResult();
                        _result = result;
                    }

                    diagnosticSource.BeforeActionResult(controllerContext, result);

                    try
                    {
                        await _result.ExecuteResultAsync(controllerContext);
                    }
                    finally
                    {
                        diagnosticSource.AfterActionResult(controllerContext, result);
                    }
                }
            }
            finally
            {
                // Release the instance after all filters have run. We don't need to surround
                // Authorizations filters because the instance will be created much later than
                // that.
                if (controller != null)
                {
                    _releaseController(controllerContext, controller);
                }
            }
        }

        private Task InvokeNextAuthorizationFilterAsync()
        {
            Debug.Assert(_result == null);
            Debug.Assert(_exception == null);
            Debug.Assert(_exceptionDispatchInfo == null);
            Debug.Assert(!_exceptionHandled);
            Debug.Assert(_exceptionResult == null);

            var current = _cursor.GetNextFilter<IAuthorizationFilter, IAsyncAuthorizationFilter>();
            if (current.FilterAsync != null)
            {
                return InvokeAsyncAuthorizationFilterAsync(current.FilterAsync);
            }
            else if (current.Filter != null)
            {
                return InvokeSyncAuthorizationFilterAsync(current.Filter);
            }
            else
            {
                // We've run out of Authorization Filters - if we haven't short circuited by now then this
                // request is authorized.
                return TaskCache.CompletedTask;
            }
        }

        private async Task InvokeAsyncAuthorizationFilterAsync(IAsyncAuthorizationFilter filter)
        {
            if (_authorizationContext == null)
            {
                _authorizationContext = new PrivateAuthorizationFilterContext(this);
            }

            _diagnosticSource.BeforeOnAuthorizationAsync(_authorizationContext, filter);

            await filter.OnAuthorizationAsync(_authorizationContext);

            _diagnosticSource.AfterOnAuthorizationAsync(_authorizationContext, filter);

            if (_result != null)
            {
                _logger.AuthorizationFailure(filter);
                return;
            }

            // Only keep going if we don't have a result
            await InvokeNextAuthorizationFilterAsync();
        }

        private async Task InvokeSyncAuthorizationFilterAsync(IAuthorizationFilter filter)
        {
            if (_authorizationContext == null)
            {
                _authorizationContext = new PrivateAuthorizationFilterContext(this);
            }

            _diagnosticSource.BeforeOnAuthorization(_authorizationContext, filter);

            filter.OnAuthorization(_authorizationContext);

            _diagnosticSource.AfterOnAuthorization(_authorizationContext, filter);

            if (_result != null)
            {
                _logger.AuthorizationFailure(filter);
                return;
            }

            // Only keep going if we don't have a result
            await InvokeNextAuthorizationFilterAsync();
        }

        private Task InvokeNextResourceFilterAsync()
        {
            Debug.Assert(_result == null);
            Debug.Assert(_exception == null);
            Debug.Assert(_exceptionDispatchInfo == null);
            Debug.Assert(!_exceptionHandled);
            Debug.Assert(_exceptionResult == null);

            var current = _cursor.GetNextFilter<IResourceFilter, IAsyncResourceFilter>();
            if (current.FilterAsync != null)
            {
                return InvokeAsyncResourceFilterAsync(current.FilterAsync);
            }
            else if (current.Filter != null)
            {
                return InvokeSyncResourceFilterAsync(current.Filter);
            }
            else if (_resourceExecutingContext != null)
            {
                return InvokeExceptionFiltersInsideResourceFilter();
            }
            else
            {
                return TaskCache.CompletedTask;
            }
        }

        private async Task<ResourceExecutedContext> InvokeNextResourceFilterAwaitedAsync()
        {
            Debug.Assert(_resourceExecutingContext != null);

            if (_result != null)
            {
                // If we get here, it means that an async filter set a result AND called next(). This is forbidden.
                var message = Resources.FormatAsyncResourceFilter_InvalidShortCircuit(
                    typeof(IAsyncResourceFilter).Name,
                    nameof(ResourceExecutingContext.Result),
                    typeof(ResourceExecutingContext).Name,
                    typeof(ResourceExecutionDelegate).Name);

                throw new InvalidOperationException(message);
            }

            await InvokeNextResourceFilterAsync();

            Debug.Assert(_resourceExecutedContext != null);
            return _resourceExecutedContext;
        }

        private async Task InvokeAsyncResourceFilterAsync(IAsyncResourceFilter filter)
        {
            if (_resourceExecutingContext == null)
            {
                _resourceExecutingContext = new PrivateResourceExecutingContext(this);
            }

            try
            {
                _diagnosticSource.BeforeOnResourceExecution(_resourceExecutingContext, filter);

                await filter.OnResourceExecutionAsync(_resourceExecutingContext, InvokeNextResourceFilterAwaitedAsync);

                if (_resourceExecutedContext == null)
                {
                    // If we get here then the filter didn't call 'next' indicating a short circuit. We do
                    // the error checking for that in InvokeNextResourceFilterAwaitedAsync.
                    Debug.Assert(_result != null);
                    Debug.Assert(_resourceExecutedContext == null);

                    _resourceExecutedContext = new PrivateResourceExecutedContext(this)
                    {
                        Canceled = true,
                    };
                }

                _diagnosticSource.AfterOnResourceExecution(_resourceExecutedContext, filter);

                if (_result != null)
                {
                    _logger.ResourceFilterShortCircuited(filter);

                    _diagnosticSource.BeforeActionResult(_controllerContext, _result);

                    try
                    {
                        await _result.ExecuteResultAsync(_controllerContext);
                    }
                    finally
                    {
                        _diagnosticSource.AfterActionResult(_controllerContext, _result);
                    }
                }
            }
            catch (Exception exception)
            {
                _exceptionDispatchInfo = ExceptionDispatchInfo.Capture(exception);
                _exception = exception;
                _exceptionHandled = false;

                if (_resourceExecutedContext == null)
                {
                    _resourceExecutedContext = new PrivateResourceExecutedContext(this);
                }
            }
        }

        private async Task InvokeSyncResourceFilterAsync(IResourceFilter filter)
        {
            if (_resourceExecutingContext == null)
            {
                _resourceExecutingContext = new PrivateResourceExecutingContext(this);
            }

            try
            {
                _diagnosticSource.BeforeOnResourceExecuting(_resourceExecutingContext, filter);

                filter.OnResourceExecuting(_resourceExecutingContext);

                _diagnosticSource.AfterOnResourceExecuting(_resourceExecutingContext, filter);

                if (_result != null)
                {
                    // Short-circuited by setting a result.
                    _logger.ResourceFilterShortCircuited(filter);

                    _diagnosticSource.BeforeActionResult(_controllerContext, _result);

                    try
                    {
                        await _result.ExecuteResultAsync(_controllerContext);
                    }
                    finally
                    {
                        _diagnosticSource.AfterActionResult(_controllerContext, _result);
                    }

                    Debug.Assert(_result != null);
                    Debug.Assert(_resourceExecutedContext == null);

                    _resourceExecutedContext = new PrivateResourceExecutedContext(this)
                    {
                        Canceled = true,
                    };

                    return;
                }

                await InvokeNextResourceFilterAsync();

                _diagnosticSource.BeforeOnResourceExecuted(_resourceExecutedContext, filter);

                filter.OnResourceExecuted(_resourceExecutedContext);

                _diagnosticSource.AfterOnResourceExecuted(_resourceExecutedContext, filter);
            }
            catch (Exception exception)
            {
                _exceptionDispatchInfo = ExceptionDispatchInfo.Capture(exception);
                _exception = exception;
                _exceptionHandled = false;

                if (_resourceExecutedContext == null)
                {
                    _resourceExecutedContext = new PrivateResourceExecutedContext(this);
                }
            }
        }

        private async Task InvokeExceptionFiltersInsideResourceFilter()
        {
            Debug.Assert(_resourceExecutingContext != null);
            Debug.Assert(_resourceExecutedContext == null);
            Debug.Assert(_result == null);
            Debug.Assert(_exception == null);
            Debug.Assert(_exceptionDispatchInfo == null);
            Debug.Assert(!_exceptionHandled);
            Debug.Assert(_exceptionResult == null);

            _resourceExecutedContext = new PrivateResourceExecutedContext(this);

            try
            {
                // >> ExceptionFilters >> Model Binding >> ActionFilters >> Action
                _cursor.Reset();
                await InvokeNextExceptionFilterAsync();

                if (_exceptionContext != null)
                {
                    // This means we executed exception filters. We need to handle a short circuit result as well as
                    // any unhandled exceptions. Either of these cases will end the pipeline.
                    if (_exceptionResult != null)
                    {
                        _exception = null;
                        _exceptionDispatchInfo = null;

                        _result = _exceptionResult;
                        _exceptionResult = null;

                        // If Exception Filters provide a result, it's a short-circuit to 'handle' an exception.
                        // We don't execute Result Filters around the result since it's not a 'normal' result.
                        _diagnosticSource.BeforeActionResult(_controllerContext, _result);

                        try
                        {
                            await _result.ExecuteResultAsync(_controllerContext);
                        }
                        finally
                        {
                            _diagnosticSource.AfterActionResult(_controllerContext, _result);
                        }

                        return;
                    }
                    else if (_exception != null)
                    {
                        // If we get here, this means that we have an unhandled exception.
                        // Exception filters didn't handle this, so send it on to resource filters.
                        return;
                    }
                }
                else
                {
                    // If we get here then this means that we didn't have any exception filters. We need to 
                    // run action filters and possibly the action itself.
                    _controller = _createController(_controllerContext);

                    _arguments = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    await _controllerArgumentBinder.BindArgumentsAsync(_controllerContext, _controller, _arguments);

                    _cursor.Reset();
                    await InvokeNextActionFilterAsync();

                    if (_actionExecutedContext != null)
                    {
                        // This means we executed action filters. We need to rethrow any unhandled exceptions, since an
                        // unhandled exception in an action filter would prevent the result from running.
                        if (_exceptionHandled)
                        {
                            _exception = null;
                            _exceptionDispatchInfo = null;
                            _exceptionHandled = false;
                        }

                        if (_exception != null)
                        {
                            // If we get here, this means that we have an unhandled exception.
                            // Action filters didn't handle this, so send it on to resource filters.
                            return;
                        }
                    }
                    else
                    {
                        // If we get here then this means that we didn't have any action filters. We need to 
                        // run the action itself.
                        try
                        {
                            _diagnosticSource.BeforeActionMethod(_controllerContext, _arguments, _controller);

                            var method = _controllerContext.ActionDescriptor.MethodInfo;

                            var arguments = ControllerActionExecutor.PrepareArguments(_arguments, method.GetParameters());

                            _logger.ActionMethodExecuting(_actionExecutingContext, arguments);

                            var actionReturnValue = await ControllerActionExecutor.ExecuteAsync(
                                _executor,
                                _controller,
                                arguments);

                            _result = CreateActionResult(method.ReturnType, actionReturnValue);

                            _logger.ActionMethodExecuted(_actionExecutingContext, _result);
                        }
                        finally
                        {
                            _diagnosticSource.AfterActionMethod(
                                _controllerContext,
                                _arguments,
                                _controller,
                                _result);
                        }
                    }
                }

                // We have a successful 'result' from the action or an Action Filter, so run
                // Result Filters.
                //
                // >> ResultFilters >> (Result)
                _cursor.Reset();
                await InvokeNextResultFilterAsync();

                if (_resultExecutedContext != null)
                {
                    // This means we executed result filters. There's nothing we really need to do other
                    // than silence handled exceptions.
                    if (_exceptionHandled)
                    {
                        _exception = null;
                        _exceptionDispatchInfo = null;
                        _exceptionHandled = false;
                    }

                    return;
                }
                else
                {
                    // If we get here then this means that we didn't have any result filters. We need to 
                    // run the result itself.
                    if (_result == null)
                    {
                        _result = new EmptyResult();
                    }

                    _diagnosticSource.BeforeActionResult(_controllerContext, _result);

                    try
                    {
                        await _result.ExecuteResultAsync(_controllerContext);
                    }
                    finally
                    {
                        _diagnosticSource.AfterActionResult(_controllerContext, _result);
                    }
                }
            }
            catch (Exception exception)
            {
                _exceptionDispatchInfo = ExceptionDispatchInfo.Capture(exception);
                _exception = exception;
                _exceptionHandled = false;
            }
        }

        private Task InvokeNextExceptionFilterAsync()
        {
            Debug.Assert(_result == null);
            Debug.Assert(_exception == null);
            Debug.Assert(_exceptionDispatchInfo == null);
            Debug.Assert(!_exceptionHandled);
            Debug.Assert(_exceptionResult == null);

            var current = _cursor.GetNextFilter<IExceptionFilter, IAsyncExceptionFilter>();
            if (current.FilterAsync != null)
            {
                return InvokeAsyncExceptionFilterAsync(current.FilterAsync);
            }
            else if (current.Filter != null)
            {
                return InvokeSyncExceptionFilterAsync(current.Filter);
            }
            else if (_exceptionContext != null)
            {
                return InvokeActionFiltersInsideExceptionFilter();
            }
            else
            {
                return TaskCache.CompletedTask;
            }
        }

        private async Task InvokeAsyncExceptionFilterAsync(IAsyncExceptionFilter filter)
        {
            // We need to pre-create the ExceptionContext so that the stack will be preserved
            // when running action filters.
            if (_exceptionContext == null)
            {
                _exceptionContext = new PrivateExceptionContext(this);
            }

            // Exception filters run "on the way out" - so the filter is run after the rest of the
            // pipeline.
            await InvokeNextExceptionFilterAsync();

            if (_exception != null)
            {
                _diagnosticSource.BeforeOnExceptionAsync(_exceptionContext, filter);

                // Exception filters only run when there's an exception - unsetting it will short-circuit
                // other exception filters.
                await filter.OnExceptionAsync(_exceptionContext);

                _diagnosticSource.AfterOnExceptionAsync(_exceptionContext, filter);

                if (_exception == null)
                {
                    _logger.ExceptionFilterShortCircuited(filter);
                }
            }
        }

        private async Task InvokeSyncExceptionFilterAsync(IExceptionFilter filter)
        {
            // We need to pre-create the ExceptionContext so that the stack will be preserved
            // when running action filters.
            if (_exceptionContext == null)
            {
                _exceptionContext = new PrivateExceptionContext(this);
            }

            // Exception filters run "on the way out" - so the filter is run after the rest of the
            // pipeline.
            await InvokeNextExceptionFilterAsync();

            if (_exception != null)
            {
                _diagnosticSource.BeforeOnException(_exceptionContext, filter);

                // Exception filters only run when there's an exception - unsetting it will short-circuit
                // other exception filters.
                filter.OnException(_exceptionContext);

                _diagnosticSource.AfterOnException(_exceptionContext, filter);

                if (_exception == null)
                {
                    _logger.ExceptionFilterShortCircuited(filter);
                }
            }
        }

        private async Task InvokeActionFiltersInsideExceptionFilter()
        {
            Debug.Assert(_exceptionContext != null);
            Debug.Assert(_result == null);
            Debug.Assert(_exception == null);
            Debug.Assert(_exceptionDispatchInfo == null);
            Debug.Assert(!_exceptionHandled);
            Debug.Assert(_exceptionResult == null);

            // We've reached the 'end' of the exception filter pipeline - this means that one stack frame has
            // been built for each exception. When we return from here, these frames will either:
            //
            // 1) Call the filter (if we have an exception)
            // 2) No-op (if we don't have an exception)
            try
            {
                _controller = _createController(_controllerContext);

                _arguments = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                await _controllerArgumentBinder.BindArgumentsAsync(_controllerContext, _controller, _arguments);

                _cursor.Reset();
                await InvokeNextActionFilterAsync();

                if (_actionExecutedContext != null)
                {
                    // This means we executed action filters. We need to rethrow any unhandled exceptions, since an
                    // unhandled exception in an action filter would prevent the result from running.
                    if (_exceptionHandled)
                    {
                        _exception = null;
                        _exceptionDispatchInfo = null;
                        _exceptionHandled = false;
                    }

                    if (_exception != null)
                    {
                        // If we get here, this means that we have an unhandled exception.
                        // Action filters didn't handle this, so send it on to resource filters.
                        return;
                    }
                }
                else
                {
                    // If we get here then this means that we didn't have any action filters. We need to 
                    // run the action itself.
                    try
                    {
                        _diagnosticSource.BeforeActionMethod(_controllerContext, _arguments, _controller);

                        var method = _controllerContext.ActionDescriptor.MethodInfo;

                        var arguments = ControllerActionExecutor.PrepareArguments(_arguments, method.GetParameters());

                        _logger.ActionMethodExecuting(_actionExecutingContext, arguments);

                        var actionReturnValue = await ControllerActionExecutor.ExecuteAsync(
                            _executor,
                            _controller,
                            arguments);

                        _result = CreateActionResult(method.ReturnType, actionReturnValue);

                        _logger.ActionMethodExecuted(_actionExecutingContext, _result);
                    }
                    finally
                    {
                        _diagnosticSource.AfterActionMethod(
                            _controllerContext,
                            _arguments,
                            _controller,
                            _result);
                    }
                }
            }
            catch (Exception exception)
            {
                _exceptionDispatchInfo = ExceptionDispatchInfo.Capture(exception);
                _exception = exception;
                _exceptionHandled = false;
            }
        }

        private Task InvokeNextActionFilterAsync()
        {
            Debug.Assert(_result == null);
            Debug.Assert(_exception == null);
            Debug.Assert(_exceptionDispatchInfo == null);
            Debug.Assert(!_exceptionHandled);
            Debug.Assert(_exceptionResult == null);

            var current = _cursor.GetNextFilter<IActionFilter, IAsyncActionFilter>();
            if (current.FilterAsync != null)
            {
                return InvokeAsyncActionFilterAsync(current.FilterAsync);
            }
            else if (current.Filter != null)
            {
                return InvokeSyncActionFilterAsync(current.Filter);
            }
            else if (_actionExecutingContext != null)
            {
                return InvokeActionInsideActionFilter();
            }
            else
            {
                return TaskCache.CompletedTask;
            }
        }

        private async Task<ActionExecutedContext> InvokeNextActionFilterAwaitedAsync()
        {
            if (_result != null)
            {
                // If we get here, it means that an async filter set a result AND called next(). This is forbidden.
                var message = Resources.FormatAsyncActionFilter_InvalidShortCircuit(
                    typeof(IAsyncActionFilter).Name,
                    nameof(ActionExecutingContext.Result),
                    typeof(ActionExecutingContext).Name,
                    typeof(ActionExecutionDelegate).Name);

                throw new InvalidOperationException(message);
            }

            await InvokeNextActionFilterAsync();

            Debug.Assert(_actionExecutedContext != null);
            return _actionExecutedContext;
        }

        private async Task InvokeAsyncActionFilterAsync(IAsyncActionFilter filter)
        {
            try
            {
                if (_actionExecutingContext == null)
                {
                    _actionExecutingContext = new PrivateActionExecutingContext(this);
                }

                _diagnosticSource.BeforeOnActionExecution(_actionExecutingContext, filter);

                await filter.OnActionExecutionAsync(_actionExecutingContext, InvokeNextActionFilterAwaitedAsync);

                if (_actionExecutedContext == null)
                {
                    // If we get here then the filter didn't call 'next' indicating a short circuit
                    _logger.ActionFilterShortCircuited(filter);

                    _actionExecutedContext = new PrivateActionExecutedContext(this)
                    {
                        Canceled = true,
                    };
                }

                _diagnosticSource.AfterOnActionExecution(_actionExecutedContext, filter);
            }
            catch (Exception exception)
            {
                _exceptionDispatchInfo = ExceptionDispatchInfo.Capture(exception);
                _exception = exception;
                _exceptionHandled = false;

                if (_actionExecutedContext == null)
                {
                    _actionExecutedContext = new PrivateActionExecutedContext(this);
                }
            }
        }

        private async Task InvokeSyncActionFilterAsync(IActionFilter filter)
        {
            try
            {
                if (_actionExecutingContext == null)
                {
                    _actionExecutingContext = new PrivateActionExecutingContext(this);
                }

                _diagnosticSource.BeforeOnActionExecuting(_actionExecutingContext, filter);

                filter.OnActionExecuting(_actionExecutingContext);

                _diagnosticSource.AfterOnActionExecuting(_actionExecutingContext, filter);

                if (_result != null)
                {
                    // Short-circuited by setting a result.
                    _logger.ActionFilterShortCircuited(filter);

                    _actionExecutedContext = new PrivateActionExecutedContext(this)
                    {
                        Canceled = true,
                    };

                    return;
                }

                await InvokeNextActionFilterAsync();

                _diagnosticSource.BeforeOnActionExecuted(_actionExecutedContext, filter);

                filter.OnActionExecuted(_actionExecutedContext);

                _diagnosticSource.BeforeOnActionExecuted(_actionExecutedContext, filter);
            }
            catch (Exception exception)
            {
                _exceptionDispatchInfo = ExceptionDispatchInfo.Capture(exception);
                _exception = exception;
                _exceptionHandled = false;

                if (_actionExecutedContext == null)
                {
                    _actionExecutedContext = new PrivateActionExecutedContext(this);
                }
            }
        }

        private async Task InvokeActionInsideActionFilter()
        {
            Debug.Assert(_actionExecutingContext != null);
            Debug.Assert(_actionExecutedContext == null);
            Debug.Assert(_result == null);
            Debug.Assert(_exception == null);
            Debug.Assert(_exceptionDispatchInfo == null);
            Debug.Assert(!_exceptionHandled);
            Debug.Assert(_exceptionResult == null);

            _actionExecutedContext = new PrivateActionExecutedContext(this);

            try
            {
                _diagnosticSource.BeforeActionMethod(_controllerContext, _arguments, _controller);

                var method = _controllerContext.ActionDescriptor.MethodInfo;

                var arguments = ControllerActionExecutor.PrepareArguments(_arguments, method.GetParameters());

                _logger.ActionMethodExecuting(_actionExecutingContext, arguments);

                var actionReturnValue = await ControllerActionExecutor.ExecuteAsync(
                    _executor,
                    _controller,
                    arguments);

                _result = CreateActionResult(method.ReturnType, actionReturnValue);

                _logger.ActionMethodExecuted(_actionExecutingContext, _result);
            }
            catch (Exception exception)
            {
                _exceptionDispatchInfo = ExceptionDispatchInfo.Capture(exception);
                _exception = exception;
                _exceptionHandled = false;
            }
            finally
            {
                _diagnosticSource.AfterActionMethod(
                    _controllerContext,
                    _arguments,
                    _controller,
                    _result);
            }
        }

        private Task InvokeNextResultFilterAsync()
        {
            Debug.Assert(_exception == null);
            Debug.Assert(_exceptionDispatchInfo == null);
            Debug.Assert(!_exceptionHandled);
            Debug.Assert(_exceptionResult == null);

            var current = _cursor.GetNextFilter<IResultFilter, IAsyncResultFilter>();
            if (current.FilterAsync != null)
            {
                return InvokeAsyncResultFilterAsync(current.FilterAsync);
            }
            else if (current.Filter != null)
            {
                return InvokeSyncResultFilterAsync(current.Filter);
            }
            else if (_resultExecutingContext != null)
            {
                return InvokeResultInsideResultFilterAsync();
            }
            else
            {
                return TaskCache.CompletedTask;
            }
        }

        private async Task<ResultExecutedContext> InvokeNextResultFilterAwaitedAsync()
        {
            Debug.Assert(_resultExecutingContext != null);

            if (_resultExecutingContext.Cancel)
            {
                // If we get here, it means that an async filter set cancel == true AND called next().
                // This is forbidden.
                var message = Resources.FormatAsyncResultFilter_InvalidShortCircuit(
                    typeof(IAsyncResultFilter).Name,
                    nameof(ResultExecutingContext.Cancel),
                    typeof(ResultExecutingContext).Name,
                    typeof(ResultExecutionDelegate).Name);

                throw new InvalidOperationException(message);
            }

            await InvokeNextResultFilterAsync();

            Debug.Assert(_resultExecutedContext != null);
            return _resultExecutedContext;
        }

        private async Task InvokeAsyncResultFilterAsync(IAsyncResultFilter filter)
        {
            if (_resultExecutingContext == null)
            {
                _resultExecutingContext = new PrivateResultExecutingContext(this);
            }

            try
            {
                _diagnosticSource.BeforeOnResultExecution(_resultExecutingContext, filter);

                await filter.OnResultExecutionAsync(_resultExecutingContext, InvokeNextResultFilterAwaitedAsync);

                if (_resultExecutedContext == null || _resultExecutingContext.Cancel)
                {
                    // Short-circuited by not calling next || Short-circuited by setting Cancel == true
                    _logger.ResourceFilterShortCircuited(filter);

                    _resultExecutedContext = new PrivateResultExecutedContext(this)
                    {
                        Canceled = true,
                    };
                }

                _diagnosticSource.AfterOnResultExecution(_resultExecutedContext, filter);
            }
            catch (Exception exception)
            {
                _exceptionDispatchInfo = ExceptionDispatchInfo.Capture(exception);
                _exception = exception;
                _exceptionHandled = false;

                if (_resultExecutedContext == null)
                {
                    _resultExecutedContext = new PrivateResultExecutedContext(this);
                }
            }
        }

        private async Task InvokeSyncResultFilterAsync(IResultFilter filter)
        {
            if (_resultExecutingContext == null)
            {
                _resultExecutingContext = new PrivateResultExecutingContext(this);
            }

            try
            {
                _diagnosticSource.BeforeOnResultExecuting(_resultExecutingContext, filter);

                filter.OnResultExecuting(_resultExecutingContext);

                _diagnosticSource.AfterOnResultExecuting(_resultExecutingContext, filter);

                if (_resultExecutingContext.Cancel)
                {
                    // Short-circuited by setting Cancel == true
                    _logger.ResourceFilterShortCircuited(filter);

                    _resultExecutedContext = new PrivateResultExecutedContext(this)
                    {
                        Canceled = true,
                    };

                    return;
                }

                await InvokeNextResultFilterAsync();

                _diagnosticSource.BeforeOnResultExecuted(_resultExecutedContext, filter);

                filter.OnResultExecuted(_resultExecutedContext);

                _diagnosticSource.AfterOnResultExecuted(_resultExecutedContext, filter);
            }
            catch (Exception exception)
            {
                _exceptionDispatchInfo = ExceptionDispatchInfo.Capture(exception);
                _exception = exception;
                _exceptionHandled = false;

                if (_resultExecutedContext == null)
                {
                    _resultExecutedContext = new PrivateResultExecutedContext(this);
                }
            }
        }

        private async Task InvokeResultInsideResultFilterAsync()
        {
            Debug.Assert(_resultExecutingContext != null);
            Debug.Assert(_resultExecutedContext == null);
            Debug.Assert(_exception == null);
            Debug.Assert(_exceptionDispatchInfo == null);
            Debug.Assert(!_exceptionHandled);
            Debug.Assert(_exceptionResult == null);

            _resultExecutedContext = new PrivateResultExecutedContext(this);

            if (_result == null)
            {
                _result = new EmptyResult();
            }

            try
            {
                _diagnosticSource.BeforeActionResult(_controllerContext, _result);

                try
                {
                    await _result.ExecuteResultAsync(_controllerContext);
                }
                finally
                {
                    _diagnosticSource.AfterActionResult(_controllerContext, _result);
                }
            }
            catch (Exception exception)
            {
                _exceptionDispatchInfo = ExceptionDispatchInfo.Capture(exception);
                _exception = exception;
                _exceptionHandled = false;
            }
        }

        // Marking as internal for Unit Testing purposes.
        internal static IActionResult CreateActionResult(Type declaredReturnType, object actionReturnValue)
        {
            if (declaredReturnType == null)
            {
                throw new ArgumentNullException(nameof(declaredReturnType));
            }

            // optimize common path
            var actionResult = actionReturnValue as IActionResult;
            if (actionResult != null)
            {
                return actionResult;
            }

            if (declaredReturnType == typeof(void) ||
                declaredReturnType == typeof(Task))
            {
                return new EmptyResult();
            }

            // Unwrap potential Task<T> types.
            var actualReturnType = GetTaskInnerTypeOrNull(declaredReturnType) ?? declaredReturnType;
            if (actionReturnValue == null &&
                typeof(IActionResult).IsAssignableFrom(actualReturnType))
            {
                throw new InvalidOperationException(
                    Resources.FormatActionResult_ActionReturnValueCannotBeNull(actualReturnType));
            }

            return new ObjectResult(actionReturnValue)
            {
                DeclaredType = actualReturnType
            };
        }

        private static Type GetTaskInnerTypeOrNull(Type type)
        {
            var genericType = ClosedGenericMatcher.ExtractGenericInterface(type, typeof(Task<>));

            return genericType?.GenericTypeArguments[0];
        }

        private void HandleException()
        {
            if (_exceptionHandled)
            {
                _exception = null;
                _exceptionDispatchInfo = null;
                _exceptionHandled = false;
            }
        }

        private void Rethrow()
        {
            if (_exceptionDispatchInfo != null)
            {
                _exceptionDispatchInfo.Throw();
            }

            if (_exception != null)
            {
                throw _exception;
            }
        }

        /// <summary>
        /// A one-way cursor for filters.
        /// </summary>
        /// <remarks>
        /// This will iterate the filter collection once per-stage, and skip any filters that don't have
        /// the one of interfaces that applies to the current stage.
        ///
        /// Filters are always executed in the following order, but short circuiting plays a role.
        ///
        /// Indentation reflects nesting.
        ///
        /// 1. Exception Filters
        ///     2. Authorization Filters
        ///     3. Action Filters
        ///        Action
        ///
        /// 4. Result Filters
        ///    Result
        ///
        /// </remarks>
        private struct FilterCursor
        {
            private int _index;
            private readonly IFilterMetadata[] _filters;

            public FilterCursor(int index, IFilterMetadata[] filters)
            {
                _index = index;
                _filters = filters;
            }

            public FilterCursor(IFilterMetadata[] filters)
            {
                _index = 0;
                _filters = filters;
            }

            public void Reset()
            {
                _index = 0;
            }

            public FilterCursorItem<TFilter, TFilterAsync> GetNextFilter<TFilter, TFilterAsync>()
                where TFilter : class
                where TFilterAsync : class
            {
                while (_index < _filters.Length)
                {
                    var filter = _filters[_index] as TFilter;
                    var filterAsync = _filters[_index] as TFilterAsync;

                    _index += 1;

                    if (filter != null || filterAsync != null)
                    {
                        return new FilterCursorItem<TFilter, TFilterAsync>(_index, filter, filterAsync);
                    }
                }

                return default(FilterCursorItem<TFilter, TFilterAsync>);
            }
        }

        private struct FilterCursorItem<TFilter, TFilterAsync>
        {
            public readonly int Index;
            public readonly TFilter Filter;
            public readonly TFilterAsync FilterAsync;

            public FilterCursorItem(int index, TFilter filter, TFilterAsync filterAsync)
            {
                Index = index;
                Filter = filter;
                FilterAsync = filterAsync;
            }
        }

        private class PrivateAuthorizationFilterContext : AuthorizationFilterContext
        {
            private readonly ControllerActionInvoker _invoker;

            public PrivateAuthorizationFilterContext(ControllerActionInvoker invoker)
                : base(invoker._controllerContext, invoker._filters)
            {
                _invoker = invoker;
            }

            public override IActionResult Result
            {
                get { return _invoker._result; }
                set { _invoker._result = value; }
            }
        }

        private class PrivateResourceExecutingContext : ResourceExecutingContext
        {
            private readonly ControllerActionInvoker _invoker;

            public PrivateResourceExecutingContext(ControllerActionInvoker invoker)
                : base(invoker._controllerContext, invoker._filters)
            {
                _invoker = invoker;
            }

            public override IActionResult Result
            {
                get { return _invoker._result; }
                set { _invoker._result = value; }
            }
        }

        private class PrivateResourceExecutedContext : ResourceExecutedContext
        {
            private readonly ControllerActionInvoker _invoker;

            public PrivateResourceExecutedContext(ControllerActionInvoker invoker)
                : base(invoker._controllerContext, invoker._filters)
            {
                _invoker = invoker;
            }

            public override Exception Exception
            {
                get
                {
                    if (_invoker._exceptionDispatchInfo != null)
                    {
                        return _invoker._exceptionDispatchInfo.SourceException;
                    }
                    else
                    {
                        return _invoker._exception;
                    }
                }

                set
                {
                    _invoker._exceptionDispatchInfo = null;
                    _invoker._exception = value;
                }
            }

            public override ExceptionDispatchInfo ExceptionDispatchInfo
            {
                get
                {
                    return _invoker._exceptionDispatchInfo;
                }

                set
                {
                    _invoker._exception = value?.SourceException;
                    _invoker._exceptionDispatchInfo = value;
                }
            }

            public override bool ExceptionHandled
            {
                get { return _invoker._exceptionHandled; }
                set { _invoker._exceptionHandled = value; }
            }

            public override IActionResult Result
            {
                get { return _invoker._result; }
                set { _invoker._result = value; }
            }
        }

        private class PrivateExceptionContext : ExceptionContext
        {
            private readonly ControllerActionInvoker _invoker;

            public PrivateExceptionContext(ControllerActionInvoker invoker)
                : base(invoker._controllerContext, invoker._filters)
            {
                _invoker = invoker;
            }

            public override Exception Exception
            {
                get
                {
                    if (_invoker._exceptionDispatchInfo != null)
                    {
                        return _invoker._exceptionDispatchInfo.SourceException;
                    }
                    else
                    {
                        return _invoker._exception;
                    }
                }

                set
                {
                    _invoker._exceptionDispatchInfo = null;
                    _invoker._exception = value;
                }
            }

            public override ExceptionDispatchInfo ExceptionDispatchInfo
            {
                get
                {
                    return _invoker._exceptionDispatchInfo;
                }

                set
                {
                    _invoker._exception = value?.SourceException;
                    _invoker._exceptionDispatchInfo = value;
                }
            }

            public override IActionResult Result
            {
                get { return _invoker._exceptionResult; }
                set { _invoker._exceptionResult = value; }
            }
        }

        private class PrivateActionExecutingContext : ActionExecutingContext
        {
            private readonly ControllerActionInvoker _invoker;

            public PrivateActionExecutingContext(ControllerActionInvoker invoker)
                : base(invoker._controllerContext, invoker._filters, invoker._arguments, invoker._controller)
            {
                _invoker = invoker;
            }

            public override IActionResult Result
            {
                get { return _invoker._result; }
                set { _invoker._result = value; }
            }
        }

        private class PrivateActionExecutedContext : ActionExecutedContext
        {
            private readonly ControllerActionInvoker _invoker;

            public PrivateActionExecutedContext(ControllerActionInvoker invoker)
                : base(invoker._controllerContext, invoker._filters, invoker._controller)
            {
                _invoker = invoker;
            }

            public override Exception Exception
            {
                get
                {
                    if (_invoker._exceptionDispatchInfo != null)
                    {
                        return _invoker._exceptionDispatchInfo.SourceException;
                    }
                    else
                    {
                        return _invoker._exception;
                    }
                }

                set
                {
                    _invoker._exceptionDispatchInfo = null;
                    _invoker._exception = value;
                }
            }

            public override ExceptionDispatchInfo ExceptionDispatchInfo
            {
                get
                {
                    return _invoker._exceptionDispatchInfo;
                }

                set
                {
                    _invoker._exception = value?.SourceException;
                    _invoker._exceptionDispatchInfo = value;
                }
            }

            public override bool ExceptionHandled
            {
                get { return _invoker._exceptionHandled; }
                set { _invoker._exceptionHandled = value; }
            }

            public override IActionResult Result
            {
                get { return _invoker._result; }
                set { _invoker._result = value; }
            }
        }

        private class PrivateResultExecutingContext : ResultExecutingContext
        {
            private readonly ControllerActionInvoker _invoker;

            public PrivateResultExecutingContext(ControllerActionInvoker invoker)
                : base(invoker._controllerContext, invoker._filters, invoker._result, invoker._controller)
            {
                _invoker = invoker;
            }

            public override IActionResult Result
            {
                get { return _invoker._result; }
                set
                {
                    if (_invoker != null)
                    {
                        _invoker._result = value;
                    }
                }
            }
        }

        private class PrivateResultExecutedContext : ResultExecutedContext
        {
            private readonly ControllerActionInvoker _invoker;

            public PrivateResultExecutedContext(ControllerActionInvoker invoker)
                : base(
                      invoker._controllerContext, 
                      invoker._filters, 
                      invoker._result ?? new EmptyResult(),
                      invoker._controller)
            {
                _invoker = invoker;
            }

            public override Exception Exception
            {
                get
                {
                    if (_invoker._exceptionDispatchInfo != null)
                    {
                        return _invoker._exceptionDispatchInfo.SourceException;
                    }
                    else
                    {
                        return _invoker._exception;
                    }
                }

                set
                {
                    _invoker._exceptionDispatchInfo = null;
                    _invoker._exception = value;
                }
            }

            public override ExceptionDispatchInfo ExceptionDispatchInfo
            {
                get
                {
                    return _invoker._exceptionDispatchInfo;
                }

                set
                {
                    _invoker._exception = value?.SourceException;
                    _invoker._exceptionDispatchInfo = value;
                }
            }

            public override bool ExceptionHandled
            {
                get { return _invoker._exceptionHandled; }
                set { _invoker._exceptionHandled = value; }
            }
        }
    }
}

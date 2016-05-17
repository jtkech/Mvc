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

        // Do not make this readonly, it's mutable. We don't want to make a copy.
        // https://blogs.msdn.microsoft.com/ericlippert/2008/05/14/mutating-readonly-structs/
        private FilterCursor _cursor;
        private object _controller;
        private Dictionary<string, object> _arguments;
        private IActionResult _result;

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
        }

        public virtual async Task InvokeAsync()
        {
            var next = State.InvokeBegin;
            var state = (object)null;
            var scope = Scope.Invoker;
            var isCompleted = false;

            while (!isCompleted)
            {

                try
                {
                    await Next(ref next, ref state, ref scope, ref isCompleted);
                }
                finally
                {
                    if (_controller != null)
                    {
                        _controllerFactory.ReleaseController(_controllerContext, _controller);
                    }
                }
            }
        }

        private Task Next(ref State next, ref object state, ref Scope scope, ref bool isCompleted)
        {
            switch (next)
            {
                case State.InvokeBegin:
                    {
                        goto case State.AuthorizationBegin;
                    }

                case State.AuthorizationBegin:
                    {
                        _cursor.Reset();
                        goto case State.AuthorizationNext;
                    }

                case State.AuthorizationNext:
                    {
                        var current = _cursor.GetNextFilter<IAuthorizationFilter, IAsyncAuthorizationFilter>();
                        if (current.FilterAsync != null)
                        {
                            if (_authorizationContext == null)
                            {
                                _authorizationContext = new AuthorizationFilterContext(_controllerContext, _filters);
                            }

                            state = current.FilterAsync;
                            goto case State.AuthorizationAsyncBegin;
                        }
                        else if (current.Filter != null)
                        {
                            if (_authorizationContext == null)
                            {
                                _authorizationContext = new AuthorizationFilterContext(_controllerContext, _filters);
                            }

                            state = current.Filter;
                            goto case State.AuthorizationSync;
                        }
                        else
                        {
                            goto case State.AuthorizationEnd;
                        }
                    }

                case State.AuthorizationAsyncBegin:
                    {
                        var filter = (IAsyncAuthorizationFilter)state;
                        var authorizationContext = _authorizationContext;

                        _diagnosticSource.BeforeOnAuthorizationAsync(authorizationContext, filter);

                        var task = filter.OnAuthorizationAsync(authorizationContext);
                        if (task.Status != TaskStatus.RanToCompletion)
                        {
                            next = State.AuthorizationAsyncEnd;
                            return task;
                        }

                        goto case State.AuthorizationAsyncEnd;
                    }

                case State.AuthorizationAsyncEnd:
                    {
                        var filter = (IAsyncAuthorizationFilter)state;
                        var authorizationContext = _authorizationContext;

                        _diagnosticSource.AfterOnAuthorizationAsync(authorizationContext, filter);

                        if (authorizationContext.Result != null)
                        {
                            goto case State.AuthorizationShortCircuit;
                        }

                        goto case State.AuthorizationNext;
                    }

                case State.AuthorizationSync:
                    {
                        var filter = (IAuthorizationFilter)state;
                        var authorizationContext = _authorizationContext;

                        _diagnosticSource.BeforeOnAuthorization(authorizationContext, filter);

                        filter.OnAuthorization(authorizationContext);

                        _diagnosticSource.AfterOnAuthorization(authorizationContext, filter);

                        if (authorizationContext.Result != null)
                        {
                            goto case State.AuthorizationShortCircuit;
                        }

                        goto case State.AuthorizationNext;
                    }

                case State.AuthorizationShortCircuit:
                    {
                        _logger.AuthorizationFailure((IFilterMetadata)state);

                        var task = InvokeResultAsync(_authorizationContext.Result);
                        if (task.Status != TaskStatus.RanToCompletion)
                        {
                            next = State.InvokeEnd;
                            return task;
                        }

                        goto case State.InvokeEnd;
                    }

                case State.AuthorizationEnd:
                    {
                        goto case State.ResourceBegin;
                    }

                case State.ResourceBegin:
                    {
                        _cursor.Reset();
                        goto case State.ResourceNext;
                    }

                case State.ResourceNext:
                    {
                        var current = _cursor.GetNextFilter<IResourceFilter, IAsyncResourceFilter>();
                        if (current.FilterAsync != null)
                        {
                            if (_resourceExecutingContext == null)
                            {
                                _resourceExecutingContext = new ResourceExecutingContext(_controllerContext, _filters);
                            }

                            state = current.FilterAsync;
                            goto case State.ResourceAsyncBegin;
                        }
                        else if (current.Filter != null)
                        {
                            if (_resourceExecutingContext == null)
                            {
                                _resourceExecutingContext = new ResourceExecutingContext(_controllerContext, _filters);
                            }

                            state = current.Filter;
                            goto case State.ResourceSyncBegin;
                        }
                        else
                        {
                            if (scope == Scope.Invoker && _resourceExecutingContext != null)
                            {
                                goto case State.ResourceEnd;
                            }
                            else if (scope == Scope.Invoker)
                            {
                                goto case State.ExceptionBegin;
                            }

                            Debug.Assert(_resourceExecutingContext != null);
                            goto case State.ResourceInside;
                        }
                    }

                case State.ResourceAsyncBegin:
                    {
                        var filter = (IAsyncResourceFilter)state;
                        var resourceExecutingContext = _resourceExecutingContext;

                        _diagnosticSource.BeforeOnResourceExecution(resourceExecutingContext, filter);

                        var task = filter.OnResourceExecutionAsync(resourceExecutingContext, InvokeNextResourceFilterAwaitedAsync);
                        if (task.Status != TaskStatus.RanToCompletion)
                        {
                            next = State.ResourceAsyncEnd;
                            return task;
                        }

                        goto case State.ResourceAsyncEnd;
                    }

                case State.ResourceAsyncEnd:
                    {
                        var filter = (IAsyncResourceFilter)state;
                        if (_resourceExecutedContext == null)
                        {
                            // If we get here then the filter didn't call 'next' indicating a short circuit
                            Debug.Assert(_resourceExecutingContext.Result != null);
                            _resourceExecutedContext = new ResourceExecutedContext(_resourceExecutingContext, _filters)
                            {
                                Canceled = true,
                                Result = _resourceExecutingContext.Result,
                            };
                        }

                        _diagnosticSource.AfterOnResourceExecution(_resourceExecutedContext, filter);

                        if (_resourceExecutingContext.Result != null)
                        {
                            goto case State.ResourceAsyncShortCircuit;
                        }

                        goto case State.ResourceEnd;
                    }

                case State.ResourceSyncBegin:
                    {
                        var filter = (IResourceFilter)state;
                        var resourceExecutingContext = _resourceExecutingContext;

                        _diagnosticSource.BeforeOnResourceExecuting(resourceExecutingContext, filter);

                        filter.OnResourceExecuting(resourceExecutingContext);

                        _diagnosticSource.AfterOnResourceExecuting(_resourceExecutingContext, filter);

                        if (_resourceExecutingContext.Result != null)
                        {
                            goto case State.ResourceSyncShortCircuit;
                        }

                        var task = InvokeNextResourceFilter();
                        if (task.Status != TaskStatus.RanToCompletion)
                        {
                            next = State.ResourceSyncEnd;
                            return task;
                        }

                        goto case State.ResourceSyncEnd;
                    }

                case State.ResourceSyncEnd:
                    {
                        var filter = (IResourceFilter)state;
                        var resourceExecutedContext = _resourceExecutedContext;

                        _diagnosticSource.BeforeOnResourceExecuted(resourceExecutedContext, filter);

                        filter.OnResourceExecuted(resourceExecutedContext);

                        _diagnosticSource.AfterOnResourceExecuted(resourceExecutedContext, filter);

                        goto case State.ResourceEnd;
                    }

                case State.ResourceSyncShortCircuit:
                    {
                        _logger.ResourceFilterShortCircuited((IFilterMetadata)state);

                        _resourceExecutedContext = new ResourceExecutedContext(_resourceExecutingContext, _filters)
                        {
                            Canceled = true,
                            Result = _resourceExecutingContext.Result,
                        };

                        var task = InvokeResultAsync(_resourceExecutingContext.Result);
                        if (task.Status != TaskStatus.RanToCompletion)
                        {
                            next = State.ResourceEnd;
                            return task;
                        }

                        goto case State.ResourceEnd;
                    }

                case State.ResourceAsyncShortCircuit:
                    {
                        _logger.ResourceFilterShortCircuited((IFilterMetadata)state);

                        _resourceExecutedContext = new ResourceExecutedContext(_resourceExecutingContext, _filters)
                        {
                            Canceled = true,
                            Result = _resourceExecutingContext.Result,
                        };

                        var task = InvokeResultAsync(_resourceExecutingContext.Result);
                        if (task.Status != TaskStatus.RanToCompletion)
                        {
                            next = State.ResourceEnd;
                            return task;
                        }

                        goto case State.ResourceEnd;
                    }

                case State.ResourceInside:
                    {
                        goto case State.ExceptionBegin;
                    }

                case State.ResourceEnd:
                    {
                        if (scope == Scope.Resource)
                        {
                            isCompleted = true;
                            return TaskCache.CompletedTask;
                        }

                        Debug.Assert(scope == Scope.Invoker);

                        var resourceExecutedContext = _resourceExecutedContext;
                        if (resourceExecutedContext != null && !resourceExecutedContext.ExceptionHandled)
                        {
                            if (resourceExecutedContext.ExceptionDispatchInfo != null)
                            {
                                resourceExecutedContext.ExceptionDispatchInfo.Throw();
                            }

                            if (resourceExecutedContext.Exception != null)
                            {
                                throw resourceExecutedContext.Exception;
                            }
                        }

                        goto case State.InvokeEnd;
                    }

                case State.ExceptionBegin:
                    {
                        _cursor.Reset();
                        goto case State.ExceptionNext;
                    }

                case State.ExceptionNext:
                    {
                        var current = _cursor.GetNextFilter<IExceptionFilter, IAsyncExceptionFilter>();
                        if (current.FilterAsync != null)
                        {
                            state = current.FilterAsync;
                            goto case State.ExceptionAsyncBegin;
                        }
                        else if (current.Filter != null)
                        {
                            state = current.Filter;
                            goto case State.ExceptionSyncBegin;
                        }
                        else if (scope == Scope.Exception)
                        {
                            goto case State.ExceptionInside;
                        }
                        else
                        {
                            goto case State.ActionBegin;
                        }
                    }

                case State.ExceptionAsyncBegin:
                    {
                        var task = InvokeNextExceptionFilterWithFrameAsync();
                        if (task.Status != TaskStatus.RanToCompletion)
                        {
                            next = State.ExceptionAsyncResume;
                            return task;
                        }

                        goto case State.ExceptionAsyncResume;
                    }

                case State.ExceptionAsyncResume:
                    {
                        var filter = (IAsyncExceptionFilter)state;
                        var exceptionContext = _exceptionContext;

                        if (_exceptionContext?.Exception != null)
                        {
                            _diagnosticSource.BeforeOnExceptionAsync(exceptionContext, filter);

                            // Exception filters only run when there's an exception - unsetting it will short-circuit
                            // other exception filters.
                            var task = filter.OnExceptionAsync(_exceptionContext);
                            if (task.Status != TaskStatus.RanToCompletion)
                            {
                                next = State.ExceptionAsyncEnd;
                                return task;
                            }

                            goto case State.ExceptionAsyncEnd;
                        }

                        goto case State.ExceptionEnd;
                    }

                case State.ExceptionAsyncEnd:
                    {
                        var filter = (IAsyncExceptionFilter)state;
                        var exceptionContext = _exceptionContext;

                        _diagnosticSource.AfterOnExceptionAsync(_exceptionContext, filter);

                        if (_exceptionContext.Exception == null)
                        {
                            _logger.ExceptionFilterShortCircuited(filter);
                        }

                        goto case State.ExceptionEnd;
                    }

                case State.ExceptionSyncBegin:
                    {
                        var task = InvokeNextExceptionFilterWithFrameAsync();
                        if (task.Status != TaskStatus.RanToCompletion)
                        {
                            next = State.ExceptionSyncEnd;
                            return task;
                        }

                        goto case State.ExceptionSyncEnd;
                    }

                case State.ExceptionSyncEnd:
                    {
                        var filter = (IExceptionFilter)state;
                        var exceptionContext = _exceptionContext;

                        if (_exceptionContext?.Exception != null)
                        {

                            _diagnosticSource.BeforeOnException(exceptionContext, filter);

                            // Exception filters only run when there's an exception - unsetting it will short-circuit
                            // other exception filters.
                            filter.OnException(_exceptionContext);

                            _diagnosticSource.AfterOnException(_exceptionContext, filter);

                            if (_exceptionContext.Exception == null)
                            {
                                _logger.ExceptionFilterShortCircuited(filter);
                            }
                        }

                        goto case State.ExceptionEnd;
                    }

                case State.ExceptionInside:
                    {
                        goto case State.ActionBegin;
                    }

                case State.ExceptionShortCircuit:
                    {
                        if (scope == Scope.Resource)
                        {
                            Debug.Assert(_exceptionContext.Result != null);
                            _resourceExecutedContext = new ResourceExecutedContext(_controllerContext, _filters)
                            {
                                Result = _exceptionContext.Result,
                            };

                            next = State.ResourceEnd;
                            return InvokeResultAsync(_exceptionContext.Result);
                        }

                        next = State.InvokeEnd;
                        return InvokeResultAsync(_exceptionContext.Result);
                    }

                case State.ExceptionEnd:
                    {
                        var exceptionContext = _exceptionContext;

                        if (scope == Scope.Exception)
                        {
                            isCompleted = true;
                            return TaskCache.CompletedTask;
                        }

                        if (exceptionContext != null)
                        {
                            if (exceptionContext.Result != null)
                            {
                                goto case State.ExceptionShortCircuit;
                            }

                            if (exceptionContext.ExceptionDispatchInfo != null)
                            {
                                exceptionContext.ExceptionDispatchInfo.Throw();
                            }

                            if (exceptionContext.Exception != null)
                            {
                                throw exceptionContext.Exception;
                            }
                        }

                        if (_actionExecutedContext != null)
                        {
                            state = _actionExecutedContext.Result;
                        }
                        else
                        {
                            state = _result;
                        }
                        
                        goto case State.ResultBegin;
                    }

                case State.ActionBegin:
                    {
                        _cursor.Reset();

                        _controller = _controllerFactory.CreateController(_controllerContext);

                        _arguments = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                        state = _arguments;
                        var task = _controllerArgumentBinder.BindArgumentsAsync(_controllerContext, _controller, _arguments);
                        if (task.Status != TaskStatus.RanToCompletion)
                        {
                            next = State.ActionNext;
                            return task;
                        }

                        goto case State.ActionNext;
                    }

                case State.ActionNext:
                    {
                        var current = _cursor.GetNextFilter<IActionFilter, IAsyncActionFilter>();
                        if (current.FilterAsync != null)
                        {
                            if (_actionExecutingContext == null)
                            {
                                var arguments = (Dictionary<string, object>)state;
                                _actionExecutingContext = new ActionExecutingContext(_controllerContext, _filters, arguments, _controller);
                            }

                            state = current.FilterAsync;
                            goto case State.ActionAsyncBegin;
                        }
                        else if (current.Filter != null)
                        {
                            if (_actionExecutingContext == null)
                            {
                                var arguments = (Dictionary<string, object>)state;
                                _actionExecutingContext = new ActionExecutingContext(_controllerContext, _filters, arguments, _controller);
                            }

                            state = current.Filter;
                            goto case State.ActionSyncBegin;
                        }
                        else
                        {
                            goto case State.ActionInside;
                        }
                    }

                case State.ActionAsyncBegin:
                    {
                        var filter = (IAsyncActionFilter)state;
                        var actionExecutingContext = _actionExecutingContext;

                        _diagnosticSource.BeforeOnActionExecution(actionExecutingContext, filter);

                        var task = filter.OnActionExecutionAsync(actionExecutingContext, InvokeNextActionFilterAwaitedAsync);
                        if (task.Status != TaskStatus.RanToCompletion)
                        {
                            next = State.ActionAsyncEnd;
                            return task;
                        }

                        goto case State.ActionAsyncEnd;
                    }

                case State.ActionAsyncEnd:
                    {
                        var filter = (IAsyncActionFilter)state;
                        var actionExecutedContext = _actionExecutedContext;

                        if (_actionExecutedContext == null)
                        {
                            // If we get here then the filter didn't call 'next' indicating a short circuit
                            _logger.ActionFilterShortCircuited(filter);

                            _actionExecutedContext = new ActionExecutedContext(
                                _actionExecutingContext,
                                _filters,
                                _controller)
                            {
                                Canceled = true,
                                Result = _actionExecutingContext.Result,
                            };
                        }

                        _diagnosticSource.AfterOnActionExecution(_actionExecutedContext, filter);

                        goto case State.ActionEnd;
                    }

                case State.ActionSyncBegin:
                    {
                        var filter = (IActionFilter)state;
                        var actionExecutingContext = _actionExecutingContext;

                        _diagnosticSource.BeforeOnActionExecuting(actionExecutingContext, filter);

                        filter.OnActionExecuting(actionExecutingContext);

                        _diagnosticSource.AfterOnActionExecuting(actionExecutingContext, filter);

                        if (actionExecutingContext.Result != null)
                        {
                            // Short-circuited by setting a result.
                            _logger.ActionFilterShortCircuited(filter);

                            _actionExecutedContext = new ActionExecutedContext(
                                _actionExecutingContext,
                                _filters,
                                _controller)
                            {
                                Canceled = true,
                                Result = _actionExecutingContext.Result,
                            };

                            goto case State.ActionEnd;
                        }

                        var task = InvokeNextActionFilterAsync();
                        if (task.Status != TaskStatus.RanToCompletion)
                        {
                            next = State.ActionSyncEnd;
                            return task;
                        }

                        goto case State.ActionSyncEnd;
                    }

                case State.ActionSyncEnd:
                    {
                        Debug.Assert(_actionExecutedContext != null);

                        var filter = (IActionFilter)state;
                        var actionExecutedContext = _actionExecutedContext;

                        _diagnosticSource.BeforeOnActionExecuted(actionExecutedContext, filter);

                        filter.OnActionExecuted(actionExecutedContext);

                        _diagnosticSource.BeforeOnActionExecuted(actionExecutedContext, filter);

                        goto case State.ActionEnd;
                    }

                case State.ActionInside:
                    {
                        next = State.ActionEnd;
                        return InvokeActionMethodAsync();
                    }

                case State.ActionEnd:
                    {
                        if (scope == Scope.Action)
                        {
                            isCompleted = true;
                            return TaskCache.CompletedTask;
                        }

                        if (scope == Scope.Exception)
                        {
                            isCompleted = true;
                            return TaskCache.CompletedTask;
                        }

                        var actionExecutedContext = _actionExecutedContext;
                        if (actionExecutedContext != null && !actionExecutedContext.ExceptionHandled)
                        {
                            if (actionExecutedContext.ExceptionDispatchInfo != null)
                            {
                                actionExecutedContext.ExceptionDispatchInfo.Throw();
                            }

                            if (actionExecutedContext.Exception != null)
                            {
                                throw actionExecutedContext.Exception;
                            }
                        }

                        if (actionExecutedContext != null)
                        {
                            _result = actionExecutedContext.Result;
                        }
                        
                        goto case State.ResultBegin;
                    }

                case State.ResultBegin:
                    {
                        _cursor.Reset();
                        goto case State.ResultNext;
                    }

                case State.ResultNext:
                    {
                        var current = _cursor.GetNextFilter<IResultFilter, IAsyncResultFilter>();
                        if (current.FilterAsync != null)
                        {
                            if (_resultExecutingContext == null)
                            {
                                _resultExecutingContext = new ResultExecutingContext(_controllerContext, _filters, _result, _controller);
                            }

                            state = current.FilterAsync;
                            goto case State.ResultAsyncBegin;
                        }
                        else if (current.Filter != null)
                        {
                            if (_resultExecutingContext == null)
                            {
                                _resultExecutingContext = new ResultExecutingContext(_controllerContext, _filters, _result, _controller);
                            }

                            state = current.Filter;
                            goto case State.ResultSyncBegin;
                        }
                        else
                        {
                            goto case State.ResultInside;
                        }
                    }

                case State.ResultAsyncBegin:
                    {
                        var filter = (IAsyncResultFilter)state;
                        var resultExecutingContext = _resultExecutingContext;

                        _diagnosticSource.BeforeOnResultExecution(resultExecutingContext, filter);

                        var task = filter.OnResultExecutionAsync(resultExecutingContext, InvokeNextResultFilterAwaitedAsync);
                        if (task.Status != TaskStatus.RanToCompletion)
                        {
                            next = State.ResourceAsyncEnd;
                            return task;
                        }

                        goto case State.ResultAsyncEnd;
                    }

                case State.ResultAsyncEnd:
                    {
                        var filter = (IAsyncResultFilter)state;
                        var resultExecutingContext = _resultExecutingContext;
                        var resultExecutedContext = _resultExecutedContext;

                        if (resultExecutedContext == null || resultExecutingContext.Cancel == true)
                        {
                            // Short-circuited by not calling next || Short-circuited by setting Cancel == true
                            _logger.ResourceFilterShortCircuited(filter);

                            _resultExecutedContext = new ResultExecutedContext(
                                _controllerContext,
                                _filters,
                                resultExecutingContext.Result,
                                _controller)
                            {
                                Canceled = true,
                            };
                        }

                        _diagnosticSource.AfterOnResultExecution(_resultExecutedContext, filter);
                        goto case State.ResultEnd;
                    }

                case State.ResultSyncBegin:
                    {
                        var filter = (IResultFilter)state;
                        var resultExecutingContext = _resultExecutingContext;

                        _diagnosticSource.BeforeOnResultExecuting(resultExecutingContext, filter);

                        filter.OnResultExecuting(resultExecutingContext);

                        _diagnosticSource.AfterOnResultExecuting(resultExecutingContext, filter);

                        if (_resultExecutingContext.Cancel == true)
                        {
                            // Short-circuited by setting Cancel == true
                            _logger.ResourceFilterShortCircuited(filter);

                            _resultExecutedContext = new ResultExecutedContext(
                                resultExecutingContext,
                                _filters,
                                resultExecutingContext.Result,
                                _controller)
                            {
                                Canceled = true,
                            };

                            goto case State.ResultEnd;
                        }

                        var task = InvokeNextResultFilterAsync();
                        if (task.Status != TaskStatus.RanToCompletion)
                        {
                            next = State.ResultSyncEnd;
                            return task;
                        }

                        goto case State.ResultSyncEnd;
                    }

                case State.ResultSyncEnd:
                    {
                        Debug.Assert(_resultExecutedContext != null);

                        var filter = (IResultFilter)state;
                        var resultExecutedContext = _resultExecutedContext;


                        _diagnosticSource.BeforeOnResultExecuted(resultExecutedContext, filter);

                        filter.OnResultExecuted(resultExecutedContext);

                        _diagnosticSource.AfterOnResultExecuted(resultExecutedContext, filter);

                        goto case State.ResultEnd;
                    }

                case State.ResultInside:
                    {
                        // The empty result is always flowed back as the 'executed' result
                        var result = _resultExecutingContext?.Result ?? _result ?? new EmptyResult();
                        _result = result;

                        var task = InvokeResultAsync(_result);
                        if (task.Status != TaskStatus.RanToCompletion)
                        {
                            next = State.ResultEnd;
                            return task;
                        }

                        goto case State.ResultEnd;
                    }

                case State.ResultEnd:
                    {
                        var result = _result;

                        if (scope == Scope.Result)
                        {
                            if (_resultExecutedContext == null)
                            {
                                _resultExecutedContext = new ResultExecutedContext(_controllerContext, _filters, result, _controller);
                            }

                            isCompleted = true;
                            return TaskCache.CompletedTask;
                        }

                        if (_resultExecutedContext != null)
                        {
                            if (_resultExecutedContext.Exception != null && !_resultExecutedContext.ExceptionHandled)
                            {
                                // There's an unhandled exception in filters
                                if (_resultExecutedContext.ExceptionDispatchInfo != null)
                                {
                                    _resultExecutedContext.ExceptionDispatchInfo.Throw();
                                }
                                else
                                {
                                    throw _resultExecutedContext.Exception;
                                }
                            }
                        }

                        if (scope == Scope.Resource)
                        {
                            _resourceExecutedContext = new ResourceExecutedContext(_controllerContext, _filters)
                            {
                                Result = result,
                            };

                            goto case State.ResourceEnd;
                        }

                        goto case State.InvokeEnd;
                    }

                case State.InvokeEnd:
                    {
                        isCompleted = true;
                        return TaskCache.CompletedTask;
                    }

                default:
                    throw new InvalidOperationException();
            }
        }

        private async Task InvokeNextResourceFilter()
        {
            try
            {
                var next = State.ResourceNext;
                var state = (object)null;
                var scope = Scope.Resource;
                var isCompleted = false;
                while (!isCompleted)
                {
                    await Next(ref next, ref state, ref scope, ref isCompleted);
                }
            }
            catch (Exception exception)
            {
                _resourceExecutedContext = new ResourceExecutedContext(_resourceExecutingContext, _filters)
                {
                    ExceptionDispatchInfo = ExceptionDispatchInfo.Capture(exception),
                };
            }

            Debug.Assert(_resourceExecutedContext != null);
        }

        private async Task<ResourceExecutedContext> InvokeNextResourceFilterAwaitedAsync()
        {
            Debug.Assert(_resourceExecutingContext != null);

            if (_resourceExecutingContext.Result != null)
            {
                // If we get here, it means that an async filter set a result AND called next(). This is forbidden.
                var message = Resources.FormatAsyncResourceFilter_InvalidShortCircuit(
                    typeof(IAsyncResourceFilter).Name,
                    nameof(ResourceExecutingContext.Result),
                    typeof(ResourceExecutingContext).Name,
                    typeof(ResourceExecutionDelegate).Name);

                throw new InvalidOperationException(message);
            }

            await InvokeNextResourceFilter();

            Debug.Assert(_resourceExecutedContext != null);
            return _resourceExecutedContext;
        }

        private async Task InvokeNextExceptionFilterWithFrameAsync()
        {
            try
            {
                var next = State.ExceptionNext;
                var state = (object)null;
                var scope = Scope.Exception;
                var isCompleted = false;
                while (!isCompleted)
                {
                    await Next(ref next, ref state, ref scope, ref isCompleted);
                }
            }
            catch (Exception exception)
            {
                _exceptionContext = new ExceptionContext(_controllerContext, _filters)
                {
                    ExceptionDispatchInfo = ExceptionDispatchInfo.Capture(exception),
                };
            }
        }

        private async Task InvokeNextActionFilterAsync()
        {
            try
            {
                var next = State.ActionNext;
                var state = (object)null;
                var scope = Scope.Action;
                var isCompleted = false;
                while (!isCompleted)
                {
                    await Next(ref next, ref state, ref scope, ref isCompleted);
                }
            }
            catch (Exception exception)
            {
                _actionExecutedContext = new ActionExecutedContext(_controllerContext, _filters, _controller)
                {
                    ExceptionDispatchInfo = ExceptionDispatchInfo.Capture(exception),
                };
            }

            Debug.Assert(_actionExecutedContext != null);
        }

        private async Task<ActionExecutedContext> InvokeNextActionFilterAwaitedAsync()
        {
            Debug.Assert(_actionExecutingContext != null);
            if (_actionExecutingContext.Result != null)
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

        private async Task InvokeActionMethodAsync()
        {
            var controllerContext = _controllerContext;
            var arguments = _arguments;
            var controller = _controller;

            try
            {
                _diagnosticSource.BeforeActionMethod(
                    controllerContext,
                    arguments,
                    controller);

                var actionMethodInfo = controllerContext.ActionDescriptor.MethodInfo;

                var ordered = ControllerActionExecutor.PrepareArguments(
                    arguments,
                    _executor);

                _logger.ActionMethodExecuting(controllerContext, ordered);

                var actionReturnValue = await ControllerActionExecutor.ExecuteAsync(
                    _executor,
                    controller,
                    ordered);

                _result = CreateActionResult(actionMethodInfo.ReturnType, actionReturnValue);

                _logger.ActionMethodExecuted(controllerContext, _result);
            }
            finally
            {
                _diagnosticSource.AfterActionMethod(
                    controllerContext,
                    arguments,
                    controllerContext,
                    _result);
            }

            _actionExecutedContext = new ActionExecutedContext(
                controllerContext,
                _filters,
                controller)
            {
                Result = _result
            };
        }

        private async Task InvokeNextResultFilterAsync()
        {
            try
            {
                var next = State.ResultNext;
                var state = (object)null;
                var scope = Scope.Result;
                var isCompleted = false;
                while (!isCompleted)
                {
                    await Next(ref next, ref state, ref scope, ref isCompleted);
                }
            }
            catch (Exception exception)
            {
                _resultExecutedContext = new ResultExecutedContext(_controllerContext, _filters, _result, _controller)
                {
                    ExceptionDispatchInfo = ExceptionDispatchInfo.Capture(exception),
                };
            }

            Debug.Assert(_resultExecutedContext != null);
        }

        private async Task<ResultExecutedContext> InvokeNextResultFilterAwaitedAsync()
        {
            Debug.Assert(_resultExecutingContext != null);
            if (_resultExecutingContext.Cancel == true)
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

        private async Task InvokeResultAsync(IActionResult result)
        {
            _diagnosticSource.BeforeActionResult(_controllerContext, result);

            try
            {
                await result.ExecuteResultAsync(_controllerContext);
            }
            finally
            {
                _diagnosticSource.AfterActionResult(_controllerContext, result);
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


        private enum Scope
        {
            Invoker,
            Resource,
            Exception,
            Action,
            Result,
        }

        private enum State
        {
            InvokeBegin,
            AuthorizationBegin,
            AuthorizationNext,
            AuthorizationAsyncBegin,
            AuthorizationAsyncEnd,
            AuthorizationSync,
            AuthorizationShortCircuit,
            AuthorizationEnd,
            ResourceBegin,
            ResourceNext,
            ResourceAsyncBegin,
            ResourceAsyncEnd,
            ResourceSyncBegin,
            ResourceSyncEnd,
            ResourceSyncShortCircuit,
            ResourceAsyncShortCircuit,
            ResourceInside,
            ResourceEnd,
            ExceptionBegin,
            ExceptionNext,
            ExceptionAsyncBegin,
            ExceptionAsyncResume,
            ExceptionAsyncEnd,
            ExceptionSyncBegin,
            ExceptionSyncEnd,
            ExceptionInside,
            ExceptionShortCircuit,
            ExceptionEnd,
            ActionBegin,
            ActionNext,
            ActionAsyncBegin,
            ActionAsyncEnd,
            ActionSyncBegin,
            ActionSyncEnd,
            ActionInside,
            ActionEnd,
            ResultBegin,
            ResultNext,
            ResultAsyncBegin,
            ResultAsyncEnd,
            ResultSyncBegin,
            ResultSyncEnd,
            ResultInside,
            ResultEnd,
            InvokeEnd,
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
    }
}

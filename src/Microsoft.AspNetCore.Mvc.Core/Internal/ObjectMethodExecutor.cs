// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Core;
using Microsoft.Extensions.Internal;

namespace Microsoft.AspNetCore.Mvc.Internal
{
    public class ObjectMethodExecutor
    {
        private object[] _parameterDefaultValues;
        private ActionExecutorAsync _executorAsync;
        private ActionExecutor _executor;

        private static readonly MethodInfo _convertOfTMethod =
            typeof(ObjectMethodExecutor).GetRuntimeMethods().Single(methodInfo => methodInfo.Name == nameof(ObjectMethodExecutor.Convert));

        private ObjectMethodExecutor(MethodInfo methodInfo, TypeInfo targetTypeInfo)
        {            
            if (methodInfo == null)
            {
                throw new ArgumentNullException(nameof(methodInfo));
            }
            MethodInfo = methodInfo;
            TargetTypeInfo = targetTypeInfo;
            ActionParameters = methodInfo.GetParameters();
        }

        private delegate Task<IActionResult> ActionExecutorAsync(object target, object[] parameters);

        private delegate object ActionExecutor(object target, object[] parameters);

        private delegate void VoidActionExecutor(object target, object[] parameters);

        public MethodInfo MethodInfo { get; }

        public ParameterInfo[] ActionParameters { get; }

        public TypeInfo TargetTypeInfo { get; set; }

        public Type TaskGenericType { get; set; }

        public Type MethodReturnType { get; set; }

        public bool IsMethodAsync { get; set; }

        public bool IsTypeAssignableFromIActionResult { get; set; }

        private ActionExecutorAsync ControllerActionExecutorAsync
        {
            get
            {
                if (_executorAsync == null)
                {
                    _executorAsync = GetExecutorAsync(TaskGenericType, MethodInfo, TargetTypeInfo);
                }

                return _executorAsync;
            }
        }

        public static ObjectMethodExecutor Create(MethodInfo methodInfo, TypeInfo targetTypeInfo)
        {
            var executor = new ObjectMethodExecutor(methodInfo, targetTypeInfo);
            executor.MethodReturnType = methodInfo.ReturnType;
            executor.IsMethodAsync = typeof(Task).IsAssignableFrom(executor.MethodReturnType);
            executor.TaskGenericType = GetTaskInnerTypeOrNull(executor.MethodReturnType);
            executor.IsTypeAssignableFromIActionResult = typeof(IActionResult).IsAssignableFrom(executor.MethodReturnType);
            executor._executor = GetExecutor(methodInfo, targetTypeInfo);
            return executor;
        }

        public Task<IActionResult> ExecuteAsync(object target, object[] parameters)
        {
            return ControllerActionExecutorAsync(target, parameters);
        }

        public object Execute(object target, object[] parameters)
        {
            return _executor(target, parameters);
        }

        public object GetDefaultValueForParameter(int index)
        {
            if (index < 0 || index > ActionParameters.Length - 1)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            EnsureParameterDefaultValues();

            return _parameterDefaultValues[index];
        }

        private static ActionExecutor GetExecutor(MethodInfo methodInfo, TypeInfo targetTypeInfo)
        {
            // Parameters to executor
            var targetParameter = Expression.Parameter(typeof(object), "target");
            var parametersParameter = Expression.Parameter(typeof(object[]), "parameters");

            // Build parameter list
            var parameters = new List<Expression>();
            var paramInfos = methodInfo.GetParameters();
            for (int i = 0; i < paramInfos.Length; i++)
            {
                var paramInfo = paramInfos[i];
                var valueObj = Expression.ArrayIndex(parametersParameter, Expression.Constant(i));
                var valueCast = Expression.Convert(valueObj, paramInfo.ParameterType);

                // valueCast is "(Ti) parameters[i]"
                parameters.Add(valueCast);
            }

            // Call method
            var instanceCast = Expression.Convert(targetParameter, targetTypeInfo.AsType());
            var methodCall = Expression.Call(instanceCast, methodInfo, parameters);

            // methodCall is "((Ttarget) target) method((T0) parameters[0], (T1) parameters[1], ...)"
            // Create function
            if (methodCall.Type == typeof(void))
            {
                var lambda = Expression.Lambda<VoidActionExecutor>(methodCall, targetParameter, parametersParameter);
                var voidExecutor = lambda.Compile();
                return WrapVoidAction(voidExecutor);
            }
            else
            {
                // must coerce methodCall to match ActionExecutor signature
                var castMethodCall = Expression.Convert(methodCall, typeof(object));
                var lambda = Expression.Lambda<ActionExecutor>(castMethodCall, targetParameter, parametersParameter);
                return lambda.Compile();
            }
        }

        private static ActionExecutor WrapVoidAction(VoidActionExecutor executor)
        {
            return delegate (object target, object[] parameters)
            {
                executor(target, parameters);
                return null;
            };
        }

        private static ActionExecutorAsync GetExecutorAsync(Type taskInnerType, MethodInfo methodInfo, TypeInfo targetTypeInfo)
        {
            if (taskInnerType == null)
            {
                // This will be the case for types which have derived from Task and Task<T> or non Task types.
                throw new InvalidOperationException(Resources.FormatActionExecutor_UnexpectedTaskInstance(
                    methodInfo.Name,
                    methodInfo.DeclaringType));
            }

            // Parameters to executor
            var targetParameter = Expression.Parameter(typeof(object), "target");
            var parametersParameter = Expression.Parameter(typeof(object[]), "parameters");

            // Build parameter list
            var parameters = new List<Expression>();
            var paramInfos = methodInfo.GetParameters();
            for (int i = 0; i < paramInfos.Length; i++)
            {
                var paramInfo = paramInfos[i];
                var valueObj = Expression.ArrayIndex(parametersParameter, Expression.Constant(i));
                var valueCast = Expression.Convert(valueObj, paramInfo.ParameterType);

                // valueCast is "(Ti) parameters[i]"
                parameters.Add(valueCast);
            }

            // Call method
            var instanceCast = Expression.Convert(targetParameter, targetTypeInfo.AsType());
            var methodCall = Expression.Call(instanceCast, methodInfo, parameters);

            var coerceMethodCall = GetCoerceMethodCallExpression(taskInnerType, methodCall, methodInfo);
            var lambda = Expression.Lambda<ActionExecutorAsync>(coerceMethodCall, targetParameter, parametersParameter);
            return lambda.Compile();
        }

        // We need to CoerceResult as the object value returned from methodInfo.Invoke has to be cast to a Task<T>.
        // This is necessary to enable calling await on the returned task.
        // i.e we need to write the following var result = await (Task<ActualType>)mInfo.Invoke.
        // Returning Task<IActionResult> enables us to await on the result.
        private static Expression GetCoerceMethodCallExpression(
            Type taskValueType,
            MethodCallExpression methodCall,
            MethodInfo methodInfo)
        {
            var castMethodCall = Expression.Convert(methodCall, typeof(object));
            // for: public Task<T> Action()
            // constructs: return (Task<IActionResult>)Convert<T>((Task<T>)result)
            var genericMethodInfo = _convertOfTMethod.MakeGenericMethod(taskValueType);
            var genericMethodCall = Expression.Call(null, genericMethodInfo, castMethodCall);
            var convertedResult = Expression.Convert(genericMethodCall, typeof(Task<IActionResult>));
            return convertedResult;
        }

        /// <summary>
        /// Cast Task of T to Task of IActionResult
        /// </summary>
        private static async Task<IActionResult> CastToIActionResult<T>(Task<T> task)
        {
            var resultAsObject = await task;

            var result = resultAsObject as IActionResult;

            if (result != null)
            {
                return result;
            }

            if (!typeof(IActionResult).IsAssignableFrom(typeof(T)))
            {
                return new ObjectResult(resultAsObject)
                {
                    DeclaredType = typeof(T)
                };
            }

            return null;
        }

        private static Type GetTaskInnerTypeOrNull(Type type)
        {
            var genericType = ClosedGenericMatcher.ExtractGenericInterface(type, typeof(Task<>));

            return genericType?.GenericTypeArguments[0];
        }

        private static Task<IActionResult> Convert<T>(object taskAsObject)
        {
            var task = (Task<T>)taskAsObject;
            return CastToIActionResult<T>(task);
        }

        private void EnsureParameterDefaultValues()
        {
            if (_parameterDefaultValues == null)
            {
                var count = ActionParameters.Length;
                _parameterDefaultValues = new object[count];

                for (var i = 0; i < count; i++)
                {
                    var parameterInfo = ActionParameters[i];
                    object defaultValue;

                    if (parameterInfo.HasDefaultValue)
                    {
                        defaultValue = parameterInfo.DefaultValue;
                    }
                    else
                    {
                        var defaultValueAttribute = parameterInfo
                            .GetCustomAttribute<DefaultValueAttribute>(inherit: false);

                        if (defaultValueAttribute?.Value == null)
                        {
                            defaultValue = parameterInfo.ParameterType.GetTypeInfo().IsValueType
                                ? Activator.CreateInstance(parameterInfo.ParameterType)
                                : null;
                        }
                        else
                        {
                            defaultValue = defaultValueAttribute.Value;
                        }
                    }

                    _parameterDefaultValues[i] = defaultValue;
                }
            }
        }
    }
}

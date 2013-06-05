﻿#region Copyright (c) 2010 S. van Deursen
/* The Simple Injector is an easy-to-use Inversion of Control library for .NET
 * 
 * Copyright (C) 2010 S. van Deursen
 * 
 * To contact me, please visit my blog at http://www.cuttingedge.it/blogs/steven/ or mail to steven at 
 * cuttingedge.it.
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
 * associated documentation files (the "Software"), to deal in the Software without restriction, including 
 * without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell 
 * copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the 
 * following conditions:
 * 
 * The above copyright notice and this permission notice shall be included in all copies or substantial 
 * portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT 
 * LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO 
 * EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER 
 * IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE 
 * USE OR OTHER DEALINGS IN THE SOFTWARE.
*/
#endregion

namespace SimpleInjector.Extensions.Decorators
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Reflection;
    using System.Threading;

    using SimpleInjector.Advanced;
    using SimpleInjector.Lifestyles;

    /// <summary>
    /// Hooks into the building process and adds a decorator if needed.
    /// </summary>
    internal abstract class DecoratorExpressionInterceptor
    {
        private readonly DecoratorExpressionInterceptorData data;

        protected DecoratorExpressionInterceptor(DecoratorExpressionInterceptorData data)
        {
            this.data = data;
        }

        protected Container Container
        {
            get { return this.data.Container; }
        }

        protected Lifestyle Lifestyle
        {
            get { return this.data.Lifestyle; }
        }

        // The decorator type definition (possibly open generic).
        protected Type DecoratorTypeDefinition
        {
            get { return this.data.DecoratorType; }
        }

        protected Predicate<DecoratorPredicateContext> Predicate
        {
            get { return this.data.Predicate; }
        }

        protected abstract Dictionary<InstanceProducer, ServiceTypeDecoratorInfo> ThreadStaticServiceTypePredicateCache
        {
            get;
        }

        // Store a ServiceTypeDecoratorInfo object per closed service type. We have a dictionary per
        // thread for thread-safety. We need a dictionary per thread, since the ExpressionBuilt event can
        // get raised by multiple threads at the same time (especially for types resolved using
        // unregistered type resolution) and using the same dictionary could lead to duplicate entries
        // in the ServiceTypeDecoratorInfo.AppliedDecorators list. Because the ExpressionBuilt event gets 
        // raised and all delegates registered to that event will get called on the same thread and before
        // an InstanceProducer stores the Expression, we can safely store this information in a 
        // thread-static field.
        // The key for retrieving the threadLocal value is supplied by the caller. This way both the 
        // DecoratorExpressionInterceptor and the ContainerUncontrolledServiceDecoratorInterceptor can have
        // their own dictionary. This is needed because they both use the same key, but store different
        // information.
        protected Dictionary<InstanceProducer, ServiceTypeDecoratorInfo> GetThreadStaticServiceTypePredicateCacheByKey(
            object key)
        {
            lock (key)
            {
                var threadLocal =
                    (ThreadLocal<Dictionary<InstanceProducer, ServiceTypeDecoratorInfo>>)this.Container.GetItem(key);

                if (threadLocal == null)
                {
                    threadLocal = new ThreadLocal<Dictionary<InstanceProducer, ServiceTypeDecoratorInfo>>();
                    this.Container.SetItem(key, threadLocal);
                }

                return threadLocal.Value ?? (threadLocal.Value = new Dictionary<InstanceProducer, ServiceTypeDecoratorInfo>());
            }
        }

        protected bool SatisfiesPredicate(InstanceProducer registeredProducer, Type registeredServiceType,
            Expression expression, Lifestyle lifestyle)
        {
            var context = 
                this.CreatePredicateContext(registeredProducer, registeredServiceType, expression, lifestyle);

            return this.SatisfiesPredicate(context);
        }

        protected bool SatisfiesPredicate(DecoratorPredicateContext context)
        {
            return this.Predicate == null || this.Predicate(context);
        }

        protected ServiceTypeDecoratorInfo GetServiceTypeInfo(ExpressionBuiltEventArgs e)
        {
            return this.GetServiceTypeInfo(e.Expression, e.InstanceProducer, e.RegisteredServiceType, e.Lifestyle);
        }

        protected ServiceTypeDecoratorInfo GetServiceTypeInfo(Expression originalExpression,
            InstanceProducer registeredProducer, Type registeredServiceType, Lifestyle lifestyle)
        {
            // registeredProducer.ServiceType and registeredServiceType are different when called by 
            // container uncontrolled decorator. producer.ServiceType will be IEnumerable<T> and 
            // registeredServiceType will be T.
            ExpressionRegistration registration = null;

            Func<InstanceProducer> producerBuilder = () =>
            {
                registration = new ExpressionRegistration(originalExpression, null, lifestyle, this.Container);

                return new InstanceProducer(registeredServiceType, registration);
            };

            var info = this.GetServiceTypeInfo(originalExpression, registeredProducer, producerBuilder);

            if (registration != null)
            {
                registration.SetImplementationType(info.ImplementationType);
            }

            return info;
        }

        protected ServiceTypeDecoratorInfo GetServiceTypeInfo(Expression originalExpression,
            InstanceProducer registeredProducer, Func<InstanceProducer> producerBuilder)
        {
            Type registeredServiceType = registeredProducer.ServiceType;

            var predicateCache = this.ThreadStaticServiceTypePredicateCache;

            if (!predicateCache.ContainsKey(registeredProducer))
            {
                Type implementationType =
                    ExtensionHelpers.DetermineImplementationType(originalExpression, registeredServiceType);

                var producer = producerBuilder();

                predicateCache[registeredProducer] =
                    new ServiceTypeDecoratorInfo(registeredServiceType, implementationType, producer);
            }

            return predicateCache[registeredProducer];
        }

        protected KnownRelationship[] GetKnownDecoratorRelationships(Registration decoratorRegistration,
            ConstructorInfo decoratorConstructor, Type registeredServiceType, InstanceProducer decoratee)
        {
            var decorateeRelationship =
                this.GetDecorateeRelationship(decoratorConstructor, registeredServiceType, decoratee);

            var normalDependencyRelationships =
                from relationship in decoratorRegistration.GetRelationships()
                where relationship.Dependency.ServiceType != registeredServiceType
                select relationship;

            return normalDependencyRelationships.Union(decorateeRelationship).ToArray();
        }
        
        protected Registration CreateRegistration(Type serviceType, ConstructorInfo decoratorConstructor,
            Expression decorateeExpression)
        {
            ParameterInfo decorateeParameter = GetDecorateeParameter(serviceType, decoratorConstructor);

            decorateeExpression = GetExpressionForDecorateeDependencyParameterOrNull(
                decorateeParameter, serviceType, decorateeExpression);

            var overriddenParameters = new[] { Tuple.Create(decorateeParameter, decorateeExpression) };

            return this.Lifestyle.CreateRegistration(serviceType,
                decoratorConstructor.DeclaringType, this.Container, overriddenParameters);
        }

        protected static bool IsDecorateeParameter(ParameterInfo parameter, Type registeredServiceType)
        {
            return IsDecorateeDependencyParameter(parameter, registeredServiceType) ||
                IsDecorateeFactoryDependencyParameter(parameter, registeredServiceType);
        }

        protected static bool IsDecorateeFactoryDependencyParameter(ParameterInfo parameter, Type serviceType)
        {
            return parameter.ParameterType.IsGenericType &&
                parameter.ParameterType.GetGenericTypeDefinition() == typeof(Func<>) &&
                parameter.ParameterType == typeof(Func<>).MakeGenericType(serviceType);
        }

        protected DecoratorPredicateContext CreatePredicateContext(ExpressionBuiltEventArgs e)
        {
            return this.CreatePredicateContext(e.InstanceProducer, e.RegisteredServiceType, e.Expression, 
                e.Lifestyle);
        }

        protected DecoratorPredicateContext CreatePredicateContext(InstanceProducer registeredProducer,
            Type registeredServiceType, Expression expression, Lifestyle lifestyle)
        {
            var info = this.GetServiceTypeInfo(expression, registeredProducer, registeredServiceType, lifestyle);

            // NOTE: registeredServiceType can be different from registeredProducer.ServiceType.
            // This is the case for container uncontrolled collections where producer.ServiceType is the
            // IEnumerable<T> and registeredServiceType is T.
            return DecoratorPredicateContext.CreateFromInfo(registeredServiceType, expression, info);
        }

        protected static Expression GetExpressionForDecorateeDependencyParameterOrNull(
            ParameterInfo parameter, Type serviceType, Expression expression)
        {
            return
                BuildExpressionForDecorateeDependencyParameter(parameter, serviceType, expression) ??
                BuildExpressionForDecorateeFactoryDependencyParameter(parameter, serviceType, expression) ??
                null;
        }
        
        protected static ParameterInfo GetDecorateeParameter(Type serviceType, 
            ConstructorInfo decoratorConstructor)
        {
            return (
                from parameter in decoratorConstructor.GetParameters()
                where IsDecorateeParameter(parameter, serviceType)
                select parameter)
                .Single();
        }

        private KnownRelationship GetDecorateeRelationship(ConstructorInfo constructor,
            Type registeredServiceType, InstanceProducer decoratee)
        {
            return (
                from parameter in constructor.GetParameters()
                where IsDecorateeParameter(parameter, registeredServiceType)
                select new KnownRelationship(
                    implementationType: constructor.DeclaringType,
                    lifestyle: this.Lifestyle,
                    dependency: decoratee))
                .Single();
        }

        // The constructor parameter in which the decorated instance should be injected.
        private static Expression BuildExpressionForDecorateeDependencyParameter(ParameterInfo parameter,
            Type serviceType, Expression expression)
        {
            if (IsDecorateeDependencyParameter(parameter, serviceType))
            {
                return expression;
            }

            return null;
        }

        private static bool IsDecorateeDependencyParameter(ParameterInfo parameter, Type registeredServiceType)
        {
            return parameter.ParameterType == registeredServiceType;
        }

        // The constructor parameter in which the factory for creating decorated instances should be injected.
        private static Expression BuildExpressionForDecorateeFactoryDependencyParameter(
            ParameterInfo parameter, Type serviceType, Expression expression)
        {
            if (IsDecorateeFactoryDependencyParameter(parameter, serviceType))
            {
                var instanceCreator =
                    Expression.Lambda(Expression.Convert(expression, serviceType)).Compile();

                return Expression.Constant(instanceCreator);
            }

            return null;
        }
    }
}
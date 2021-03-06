﻿using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;
using VW.Reflection;

namespace VW.Serializer
{
    internal static class VowpalWabbitMultiExampleSerializerCompiler
    {
        internal static IVowpalWabbitSerializerCompiler<TExample> TryCreate<TExample>(VowpalWabbitSettings settings, List<FeatureExpression> allFeatures)
        {
            // check for _multi
            var multiFeature = allFeatures.FirstOrDefault(fe => fe.Name == VowpalWabbitConstants.MultiProperty);
            if (multiFeature == null)
                return null;

            // multi example path
            // IEnumerable<> or Array
            var adfType = InspectionHelper.GetEnumerableElementType(multiFeature.FeatureType);
            if (adfType == null)
                throw new ArgumentException("_multi property must be array or IEnumerable<>. Actual type: " + multiFeature.FeatureType);

            var compilerType = typeof(VowpalWabbitMultiExampleSerializerCompilerImpl<,>).MakeGenericType(typeof(TExample), adfType);
            return (IVowpalWabbitSerializerCompiler<TExample>)Activator.CreateInstance(compilerType, settings, allFeatures, multiFeature);
        }

        private sealed class VowpalWabbitMultiExampleSerializerCompilerImpl<TExample, TActionDependentFeature> : IVowpalWabbitSerializerCompiler<TExample>
        {
            private readonly VowpalWabbitSingleExampleSerializerCompiler<TExample> sharedSerializerCompiler;

            private readonly VowpalWabbitSingleExampleSerializerCompiler<TActionDependentFeature> adfSerializerComputer;

            private readonly Func<TExample, IEnumerable<TActionDependentFeature>> adfAccessor;

            public VowpalWabbitMultiExampleSerializerCompilerImpl(VowpalWabbitSettings settings, List<FeatureExpression> allFeatures, FeatureExpression multiFeature)
            {
                Contract.Requires(settings != null);
                Contract.Requires(allFeatures != null);
                Contract.Requires(multiFeature != null);

                var nonMultiFeatures = allFeatures.Where(fe => fe != multiFeature).ToList();

                this.sharedSerializerCompiler = nonMultiFeatures.Count == 0 ? null :
                    new VowpalWabbitSingleExampleSerializerCompiler<TExample>(
                        nonMultiFeatures,
                        settings == null ? null : settings.CustomFeaturizer,
                        !settings.EnableStringExampleGeneration);

                this.adfSerializerComputer = new VowpalWabbitSingleExampleSerializerCompiler<TActionDependentFeature>(
                    AnnotationJsonInspector.ExtractFeatures(typeof(TActionDependentFeature)),
                    settings == null ? null : settings.CustomFeaturizer,
                    !settings.EnableStringExampleGeneration);

                var exampleParameter = Expression.Parameter(typeof(TExample), "example");

                // CODE condition1 && condition2 && condition3 ...
                var condition = multiFeature.ValueValidExpressionFactories
                    .Skip(1)
                    .Aggregate(
                        multiFeature.ValueValidExpressionFactories.First()(exampleParameter),
                        (cond, factory) => Expression.AndAlso(cond, factory(exampleParameter)));

                var multiExpression = multiFeature.ValueExpressionFactory(exampleParameter);

                // CODE example => (IEnumerable<TActionDependentFeature>)(example._multi != null ? example._multi : null)
                var expr = Expression.Lambda<Func<TExample, IEnumerable<TActionDependentFeature>>>(
                        Expression.Condition(
                            condition,
                            multiExpression,
                            Expression.Constant(null, multiExpression.Type),
                            typeof(IEnumerable<TActionDependentFeature>)),
                    exampleParameter);

                this.adfAccessor = (Func<TExample, IEnumerable<TActionDependentFeature>>)expr.CompileToFunc();
            }

            public IVowpalWabbitSerializer<TExample> Create(VowpalWabbit vw)
            {
                return new VowpalWabbitMultiExampleSerializer<TExample, TActionDependentFeature>(
                    vw,
                    this.sharedSerializerCompiler != null ? this.sharedSerializerCompiler.Create(vw) : null,
                    this.adfSerializerComputer.Create(vw),
                    this.adfAccessor);
            }
        }
    }
}

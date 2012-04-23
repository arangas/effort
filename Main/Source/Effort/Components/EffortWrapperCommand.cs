﻿#region License

// Copyright (c) 2011 Effort Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is 
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

#endregion

using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Data.Common.CommandTrees;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Effort.DatabaseManagement;
using Effort.DbCommandTreeTransform;
using Effort.DbCommandTreeTransform.PostProcessing;
using Effort.Helpers;
using EFProviderWrapperToolkit;
using NMemory;
using NMemory.Diagnostics.Messages;
using NMemory.Tables;
using NMemory.StoredProcedures;

namespace Effort.Components
{
	public class EffortWrapperCommand : DbCommandWrapper
	{
		public EffortWrapperCommand(DbCommand wrappedCommand, DbCommandDefinitionWrapper commandDefinition)
			: base(wrappedCommand, commandDefinition)
		{

		}

		internal DatabaseContainer DatabaseContainer
		{
			get
			{
				return this.WrapperConnection.DatabaseContainer;
			}
		}

		public EffortWrapperConnection WrapperConnection
		{
			get
			{
				EffortWrapperConnection connection = base.Connection as EffortWrapperConnection;

				if (connection == null)
				{
					throw new InvalidOperationException();
				}

				return connection;
			}
		}

		#region Execute methods

		public override int ExecuteNonQuery()
		{
			if (this.Definition.CommandTree is DbUpdateCommandTree)
			{
				return this.PerformUpdate();
			}
			else if (this.Definition.CommandTree is DbDeleteCommandTree)
			{
				return this.PerformDelete();
			}
			else if (this.Definition.CommandTree is DbInsertCommandTree)
			{
				return this.PerformInsert();
			}

			throw new NotSupportedException();
		}

		protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
		{
			if (this.DesignMode)
			{
				return base.ExecuteDbDataReader(behavior);
			}

			if (this.Definition.CommandTree is DbInsertCommandTree)
			{
				return PerformInsert(behavior);
			}
			else if (base.Definition.CommandTree is DbQueryCommandTree)
			{
				return PerformQuery(behavior);
			}
            else if (base.Definition.CommandTree is DbUpdateCommandTree)
            {
                return PerformUpdate(behavior);
            }

			throw new NotSupportedException();
		}

		protected override DbTransaction DbTransaction
		{
			get { return null; }
			set { }
		}


		#endregion

		#region Query

		private DbDataReader PerformQuery(CommandBehavior behavior)
		{
			DbQueryCommandTree commandTree = base.Definition.CommandTree as DbQueryCommandTree;

			// Find tables
			TableScanVisitor tableScanVisitor = new TableScanVisitor();
			tableScanVisitor.Visit(commandTree.Query);

			// DbContext queries from EdmMetaData
			if (tableScanVisitor.Tables.Contains("EdmMetadata"))
			{
				return new EffortDataReader(new List<object>(), this.DatabaseContainer);
			}

			// Setup expression tranformer
			DbExpressionTransformVisitor visitor = new DbExpressionTransformVisitor(this.DatabaseContainer.TypeConverter);
			visitor.SetParameters(commandTree.Parameters.ToArray());
            visitor.TableProvider = this.DatabaseContainer;

			Expression linqExpression = null;
			IStoredProcedure procedure = null;

            // Try to retrieve from cache
			if (this.DatabaseContainer.TransformCache.TryGetValue(this.CommandText, out procedure))
			{
				this.DatabaseContainer.Logger.Write("TransformCache used.");
			}

			// This query was not cached yet
			if (procedure == null)
			{
                Stopwatch stopWatch = Stopwatch.StartNew();

				// Convert DbCommandTree to linq Expression Tree
				linqExpression = visitor.Visit(commandTree.Query);

				#region Postprocessing

				// Convert ' Nullable<?> Enumerable.Sum ' to ' Nullable<?> EnumerableNullSum.Sum '
				SumTransformerVisitor sumTransformer = new SumTransformerVisitor();
				linqExpression = sumTransformer.Visit(linqExpression);

				// Clean ' new SingleResult<>(x).FirstOrDefault() '
				ExcrescentSingleResultCleanserVisitor cleanser1 = new ExcrescentSingleResultCleanserVisitor();
				linqExpression = cleanser1.Visit(linqExpression);

				// Clean ' new AT(x).YProp '    (x is the init value of YProp)
				ExcrescentInitializationCleanserVisitor cleanser2 = new ExcrescentInitializationCleanserVisitor();
				linqExpression = cleanser2.Visit(linqExpression);

				#endregion

                // Create a stored procedure from the expression
                procedure = DatabaseReflectionHelper.CreateStoredProcedure(linqExpression, this.DatabaseContainer.Internal);

                // Cache the result
                this.DatabaseContainer.TransformCache[this.CommandText] = procedure;

                this.DatabaseContainer.Logger.Write(
                    "DbCommandTree converted in {0:0.00} ms",
                    stopWatch.Elapsed.TotalMilliseconds);
			}

            Dictionary<string, object> parameters = new Dictionary<string, object>();

            // Create a dictionary of parameters
            foreach (DbParameter dbParam in this.Parameters)
            {
                string name = dbParam.ParameterName;
                object value = dbParam.Value;

                // Find the description of the parameter
                ParameterDescription expectedParam = procedure.Parameters.FirstOrDefault(p => p.Name == dbParam.ParameterName);

                // Custom conversion
                value = this.DatabaseContainer.TypeConverter.ConvertClrValueToClrValue(value, expectedParam.Type);

                parameters.Add(name, value);
            }

            IEnumerable result = procedure.Execute(parameters);

            return new EffortDataReader(result, this.DatabaseContainer);
		}

		#endregion

		#region Update

		private int PerformUpdate()
		{
			this.EnsureOpenConnection();

			//Execute update on the database
			int? realDbRowCount = null;

			if (this.WrapperConnection.ProviderMode == ProviderModes.DatabaseAccelerator)
			{
				realDbRowCount = base.WrappedCommand.ExecuteNonQuery();
			}

			DbUpdateCommandTree commandTree = base.Definition.CommandTree as DbUpdateCommandTree;

			ITable table = null;

			Expression linqExpression = this.GetEnumeratorExpression(commandTree.Predicate, commandTree, out table);
			IQueryable entitiesToUpdate = this.CreateQuery(linqExpression);

			Type type = TypeHelper.GetElementType(table.GetType());

			//Collect the SetClause DbExpressions into a dictionary
			IDictionary<string, DbExpression> setClauses = this.GetSetClauseExpressions(commandTree.SetClauses);

			//Collection for collection member bindings
			IList<MemberBinding> memberBindings = new List<MemberBinding>();

			DbExpressionTransformVisitor transform = new DbExpressionTransformVisitor(this.DatabaseContainer.TypeConverter);

			// Setup context for the predicate
			ParameterExpression context = Expression.Parameter(type, "context");
			using (transform.CreateVariable(context, commandTree.Target.VariableName))
			{
				//Initialize member bindings
				foreach (PropertyInfo property in type.GetProperties())
				{
					Expression setter = null;

					// Check if member has set clause
					if (setClauses.ContainsKey(property.Name))
					{
						setter = transform.Visit(setClauses[property.Name]);
					}

					// If setter was found, insert it
					if (setter != null)
					{
						// Type correction
						setter = ExpressionHelper.CorrectType(setter, property.PropertyType);

						memberBindings.Add(Expression.Bind(property, setter));
					}
				}
			}

			Expression updater =
				Expression.Lambda(
					Expression.MemberInit(Expression.New(type), memberBindings),
					context);

			int rowCount = DatabaseReflectionHelper.UpdateEntities(entitiesToUpdate, updater).Count();

			// Compare the row count in accelerator mode 
			if (realDbRowCount.HasValue && rowCount != realDbRowCount.Value)
			{
				throw new InvalidOperationException();
			}

			return rowCount;
		}

        private DbDataReader PerformUpdate(CommandBehavior behavior)
        {
            this.EnsureOpenConnection();

            DbUpdateCommandTree commandTree = base.Definition.CommandTree as DbUpdateCommandTree;

            string[] returningFields = this.GetReturningFields(commandTree.Returning);
            IList<IDictionary<string, object>> returningEntities = this.ExecuteWrappedModulationCommand(behavior, returningFields);

            ITable table = null;

            Expression linqExpression = this.GetEnumeratorExpression(commandTree.Predicate, commandTree, out table);
            IQueryable entitiesToUpdate = this.CreateQuery(linqExpression);

            Type type = TypeHelper.GetElementType(table.GetType());

            //Collect the SetClause DbExpressions into a dictionary
            IDictionary<string, DbExpression> setClauses = this.GetSetClauseExpressions(commandTree.SetClauses);

            //Collection for collection member bindings
            IList<MemberBinding> memberBindings = new List<MemberBinding>();

            DbExpressionTransformVisitor transform = new DbExpressionTransformVisitor(this.DatabaseContainer.TypeConverter);

            // Setup context for the predicate
            ParameterExpression context = Expression.Parameter(type, "context");
            using (transform.CreateVariable(context, commandTree.Target.VariableName))
            {
                //Initialize member bindings
                foreach (PropertyInfo property in type.GetProperties())
                {
                    Expression setter = null;

                    // Check if member has set clause
                    if (setClauses.ContainsKey(property.Name))
                    {
                        setter = transform.Visit(setClauses[property.Name]);
                    }

                    // If setter was found, insert it
                    if (setter != null)
                    {
                        // Type correction
                        setter = ExpressionHelper.CorrectType(setter, property.PropertyType);

                        memberBindings.Add(Expression.Bind(property, setter));
                    }
                }
            }

            Expression updater =
                Expression.Lambda(
                    Expression.MemberInit(Expression.New(type), memberBindings),
                    context);
            
            IEnumerable<object> updatedEntities = DatabaseReflectionHelper.UpdateEntities(entitiesToUpdate, updater);

            foreach (object entity in updatedEntities)
            {
                this.SetReturnedValues(returningEntities, returningFields, entity);
            }

            return new EffortDataReader(returningEntities.ToArray(), returningFields, this.DatabaseContainer);
        }

		#endregion

		#region Delete

		private int PerformDelete()
		{
			this.EnsureOpenConnection();

			int? realDbRowCount = null;

			//Execute delete on the database
			if (this.WrapperConnection.ProviderMode == ProviderModes.DatabaseAccelerator)
			{
				realDbRowCount = base.ExecuteNonQuery();
			}

			DbDeleteCommandTree commandTree = base.Definition.CommandTree as DbDeleteCommandTree;

			ITable table = null;

			Expression linqExpression = this.GetEnumeratorExpression(commandTree.Predicate, commandTree, out table);
			IQueryable entitiesToDelete = this.CreateQuery(linqExpression);

			int rowCount = DatabaseReflectionHelper.DeleteEntities(entitiesToDelete);

			// Compare the row count in accelerator mode 
			if (realDbRowCount.HasValue && rowCount != realDbRowCount.Value)
			{
				throw new InvalidOperationException();
			}

			return rowCount;
		}

		#endregion

		#region Insert

		private int PerformInsert()
		{
			this.EnsureOpenConnection();

			if (WrapperConnection.ProviderMode == ProviderModes.DatabaseAccelerator)
			{
				base.WrappedCommand.ExecuteNonQuery();
			}

			DbInsertCommandTree commandTree = base.Definition.CommandTree as DbInsertCommandTree;
			// Get the source table
			ITable table = this.GetTable(commandTree);

			// Collect the SetClause DbExpressions into a dictionary
			IDictionary<string, DbExpression> setClauses = this.GetSetClauseExpressions(commandTree.SetClauses);

			// Collection for collection member bindings
			IList<MemberBinding> memberBindings = new List<MemberBinding>();
			DbExpressionTransformVisitor transform = new DbExpressionTransformVisitor(this.DatabaseContainer.TypeConverter);

			// Initialize member bindings
			foreach (PropertyInfo property in table.EntityType.GetProperties())
			{
				Expression setter = null;

				// Check if member has set clause
				if (setClauses.ContainsKey(property.Name))
				{
					setter = transform.Visit(setClauses[property.Name]);
				}

				// If setter was found, insert it
				if (setter != null)
				{
					// Type correction
					setter = ExpressionHelper.CorrectType(setter, property.PropertyType);
                    // Register binding
					memberBindings.Add(Expression.Bind(property, setter));
				}
			}

			this.CreateAndInsertEntity(table, memberBindings);

			return 1;
		}

		private DbDataReader PerformInsert(CommandBehavior behavior)
		{
			this.EnsureOpenConnection();

            DbInsertCommandTree commandTree = base.Definition.CommandTree as DbInsertCommandTree;

            // Find returning fields
            string[] returningFields = this.GetReturningFields(commandTree.Returning);
            // Execute wrapped command
            IList<IDictionary<string, object>> returningValues = this.ExecuteWrappedModulationCommand(behavior, returningFields);

            // Check if any record was returned
            if (this.WrapperConnection.ProviderMode == ProviderModes.DatabaseAccelerator && returningFields.Length < 1)
            {
                throw new InvalidOperationException("No record was inserted");
            }

            // Find NMemory table
			ITable table = this.GetTable(commandTree);

			// Collect the SetClause DbExpressions into a dictionary
			IDictionary<string, DbExpression> setClauses = this.GetSetClauseExpressions(commandTree.SetClauses);

			// Collection for collection member bindings
			IList<MemberBinding> memberBindings = new List<MemberBinding>();
			DbExpressionTransformVisitor transform = new DbExpressionTransformVisitor(this.DatabaseContainer.TypeConverter);

			// Initialize member bindings
			foreach (PropertyInfo property in table.EntityType.GetProperties())
			{
				Expression setter = null;

				// Check if member has set clause
				if (setClauses.ContainsKey(property.Name))
				{
					setter = transform.Visit(setClauses[property.Name]);

				}
				else if (returningFields.Contains(property.Name))
				{
					// In accelerator mode, the missing value is filled with the returned value
					if (this.WrapperConnection.ProviderMode == ProviderModes.DatabaseAccelerator)
					{
						setter = Expression.Constant(returningValues[0][property.Name]);
					}
				}

				// If setter was found, insert it
				if (setter != null)
				{
					// Type correction
					setter = ExpressionHelper.CorrectType(setter, property.PropertyType);
                    // Register binding
					memberBindings.Add(Expression.Bind(property, setter));
				}
			}

			object entity = CreateAndInsertEntity(table, memberBindings);

            this.SetReturnedValues(returningValues, returningFields, entity);

			return new EffortDataReader(returningValues.ToArray(), returningFields, this.DatabaseContainer);
		}

        private void SetReturnedValues(IList<IDictionary<string, object>> returningValues, string[] returningFields, object entity)
        {
            // In emulator mode, the generated values are gathered from the MMDB
            if (this.WrapperConnection.ProviderMode == ProviderModes.DatabaseEmulator)
            {
                Dictionary<string, object> newValue = new Dictionary<string, object>();

                for (int i = 0; i < returningFields.Length; i++)
                {
                    string property = returningFields[i];

                    object value = entity.GetType().GetProperty(property).GetValue(entity, null);

                    newValue[property] = this.DatabaseContainer.TypeConverter.ConvertClrValueFromClrValue(value);
                }

                returningValues.Add(newValue);
            }
        }

        private string[] GetReturningFields(DbExpression returning)
        {
            // Find the returning properties
            DbNewInstanceExpression returnExpression = returning as DbNewInstanceExpression;

            if (returnExpression == null)
            {
                throw new NotSupportedException("The type of the Returning properties is not DbNewInstanceExpression");
            }

            List<string> result = new List<string>();

            // Add the returning property names
            foreach (DbPropertyExpression propertyExpression in returnExpression.Arguments)
            {
                result.Add(propertyExpression.Property.Name);
            }

            return result.ToArray();
        }

        private IList<IDictionary<string, object>> ExecuteWrappedModulationCommand(CommandBehavior behavior, string[] returningFields)
        {
            List<IDictionary<string, object>> result = new List<IDictionary<string, object>>();

            // In accelerator mode, execute the wrapped command, and marshal the returned values
            if (this.WrapperConnection.ProviderMode == ProviderModes.DatabaseAccelerator)
            {
                using (DbDataReader reader = base.WrappedCommand.ExecuteReader(behavior))
                {
                    while(reader.Read())
                    {
                        Dictionary<string, object> value = new Dictionary<string, object>();

                        for (int i = 0; i < returningFields.Length; i++)
                        {
                            string fieldName = returningFields[i];
                            object fieldValue = reader.GetValue(reader.GetOrdinal(fieldName));

                            if (!(fieldValue is DBNull))
                            {
                                fieldValue = null;
                            }

                            value[fieldName] = value;
                        }
                    }
                }   
            }

            return result;
        }

		private ITable GetTable(DbModificationCommandTree commandTree)
		{
			DbScanExpression source = commandTree.Target.Expression as DbScanExpression;

			if (source == null)
			{
				throw new InvalidOperationException("The type of the Target property is not DbScanExpression");
			}

			return this.DatabaseContainer.Internal.GetTable(source.Target.Name);
		}

		private object CreateAndInsertEntity(ITable table, IList<MemberBinding> memberBindings)
		{
			LambdaExpression expression =
			   Expression.Lambda(
				   Expression.MemberInit(
					   Expression.New(table.EntityType),
					   memberBindings));

			Delegate factory = expression.Compile();

			object newEntity = factory.DynamicInvoke();

			((IReflectionTable)table).Insert(newEntity);

			return newEntity;
		}

		#endregion

		#region Helper methods

		private void EnsureOpenConnection()
		{
			// In accelerator mode, need to deal with wrapped connection
			if (this.WrapperConnection.ProviderMode == ProviderModes.DatabaseAccelerator &&
				this.WrappedCommand.Connection.State == ConnectionState.Closed)
			{
				base.WrappedCommand.Connection.Open();

				// Check for an ambient transaction
				var transaction = System.Transactions.Transaction.Current;

				// If DbTransaction is used, then the Transaction.Current is null
				if (transaction != null)
				{
					this.WrappedCommand.Connection.EnlistTransaction(transaction);
				}
			}
		}


		private IDictionary<string, DbExpression> GetSetClauseExpressions(IList<DbModificationClause> clauses)
		{
			IDictionary<string, DbExpression> result = new Dictionary<string, DbExpression>();

			foreach (DbSetClause setClause in clauses.Cast<DbSetClause>())
			{
				DbPropertyExpression property = setClause.Property as DbPropertyExpression;

				if (property == null)
				{
					throw new NotSupportedException(setClause.Property.ExpressionKind.ToString() + " is not supported");
				}

				result.Add(property.Property.Name, setClause.Value);
			}

			return result;
		}

		private Expression GetEnumeratorExpression(DbExpression predicate, DbModificationCommandTree commandTree, out ITable table)
		{
			DbExpressionTransformVisitor visitor = new DbExpressionTransformVisitor(this.DatabaseContainer.TypeConverter);
			visitor.SetParameters(commandTree.Parameters.ToArray());
			visitor.TableProvider = this.DatabaseContainer;

			// Get the source expression
			ConstantExpression source = visitor.Visit(commandTree.Target.Expression) as ConstantExpression;

			// This should be a constant expression
			if (source == null)
			{
				throw new InvalidOperationException();
			}

			table = source.Value as ITable;

			// Get the the type of the elements of the table
			Type elementType = TypeHelper.GetElementType(source.Type);

			// Create context
			ParameterExpression context = Expression.Parameter(elementType, "context");
			using (visitor.CreateVariable(context, commandTree.Target.VariableName))
			{
				// Create the predicate expression
				LambdaExpression predicateExpression =
					Expression.Lambda(
						visitor.Visit(predicate),
						context);

				// Create Where expression
				LinqMethodExpressionBuilder queryMethodBuilder = new LinqMethodExpressionBuilder();

				Expression result = queryMethodBuilder.Where(source, predicateExpression);

				ParameterExpression[] parameterExpressions = visitor.GetParameterExpressions();

				if (parameterExpressions.Length > 0)
				{
					result = Expression.Lambda(result, parameterExpressions);
				}

				return result;
			}
		}

		private IQueryable CreateQuery(Expression expression)
		{
			return DatabaseReflectionHelper.CreateTableQuery(expression, this.DatabaseContainer.Internal);
		}

		private static bool IsQueryable(Expression methodCall)
		{
			bool isQueryable = false;

			if (methodCall.Type == typeof(IQueryable))
			{
				isQueryable = true;
			}

			if (methodCall.Type.IsGenericType && methodCall.Type.GetGenericTypeDefinition() == typeof(IQueryable<>))
			{
				isQueryable = true;
			}

			return isQueryable;
		}

		#endregion
	}
}
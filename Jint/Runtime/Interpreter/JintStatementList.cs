﻿using System.Collections.Generic;
using Esprima.Ast;
using Jint.Native;
using Jint.Runtime.Environments;
using Jint.Runtime.Interpreter.Statements;

namespace Jint.Runtime.Interpreter
{
    internal class JintStatementList
    {
        private class Pair
        {
            internal JintStatement Statement;
            internal Completion? Value;
        }

        private readonly Engine _engine;
        private readonly Statement _statement;
        private readonly NodeList<Statement> _statements;

        private Pair[] _jintStatements;
        private bool _initialized;

        public JintStatementList(Engine engine, Statement statement, NodeList<Statement> statements)
        {
            _engine = engine;
            _statement = statement;
            _statements = statements;
        }

        private void Initialize()
        {
            var jintStatements = new Pair[_statements.Count];
            for (var i = 0; i < jintStatements.Length; i++)
            {
                var esprimaStatement = _statements[i];
                jintStatements[i] = new Pair
                {
                    Statement = JintStatement.Build(_engine, esprimaStatement),
                    // When in debug mode, don't do FastResolve: Stepping requires each statement to be actually executed.
                    Value = _engine._isDebugMode ? null : JintStatement.FastResolve(esprimaStatement)
                };
            }
            _jintStatements = jintStatements;
        }

        public Completion Execute()
        {
            if (!_initialized)
            {
                Initialize();
                _initialized = true;
            }

            if (_statement != null)
            {
                _engine._lastSyntaxNode = _statement;
                _engine.RunBeforeExecuteStatementChecks(_statement);
            }

            JintStatement s = null;
            var c = new Completion(CompletionType.Normal, null, null, _engine._lastSyntaxNode?.Location ?? default);
            Completion sl = c;

            // The value of a StatementList is the value of the last value-producing item in the StatementList
            JsValue lastValue = null;
            try
            {
                foreach (var pair in _jintStatements)
                {
                    s = pair.Statement;
                    c = pair.Value ?? s.Execute();

                    if (c.Type != CompletionType.Normal)
                    {
                        return new Completion(
                            c.Type,
                            c.Value ?? sl.Value,
                            c.Identifier,
                            c.Location);
                    }
                    sl = c;
                    lastValue = c.Value ?? lastValue;
                }
            }
            catch (JavaScriptException v)
            {
                var location = v.Location == default ? s.Location : v.Location;
                var completion = new Completion(CompletionType.Throw, v.Error, null, location);
                return completion;
            }
            catch (TypeErrorException e)
            {
                var error = _engine.Realm.Intrinsics.TypeError.Construct(new JsValue[]
                {
                    e.Message
                });
                return new Completion(CompletionType.Throw, error, null, s.Location);
            }
            catch (RangeErrorException e)
            {
                var error = _engine.Realm.Intrinsics.RangeError.Construct(new JsValue[]
                {
                    e.Message
                });
                c = new Completion(CompletionType.Throw, error, null, s.Location);
            }
            return new Completion(c.Type, lastValue ?? JsValue.Undefined, c.Identifier, c.Location);
        }

        /// <summary>
        /// https://tc39.es/ecma262/#sec-blockdeclarationinstantiation
        /// </summary>
        internal static void BlockDeclarationInstantiation(
            Engine engine,
            EnvironmentRecord env,
            List<Declaration> declarations)
        {
            var envRec = env;
            var boundNames = new List<string>();
            for (var i = 0; i < declarations.Count; i++)
            {
                var d = declarations[i];
                boundNames.Clear();
                d.GetBoundNames(boundNames);
                for (var j = 0; j < boundNames.Count; j++)
                {
                    var dn = boundNames[j];
                    if (d is VariableDeclaration { Kind: VariableDeclarationKind.Const })
                    {
                        envRec.CreateImmutableBinding(dn, strict: true);
                    }
                    else
                    {
                        envRec.CreateMutableBinding(dn, canBeDeleted: false);
                    }
                }

                if (d is FunctionDeclaration functionDeclaration)
                {
                    var fn = functionDeclaration.Id!.Name;
                    var functionDefinition = new JintFunctionDefinition(engine, functionDeclaration);
                    var fo = env._engine.Realm.Intrinsics.Function.InstantiateFunctionObject(functionDefinition, env);
                    envRec.InitializeBinding(fn, fo);
                }
            }
        }
    }
}

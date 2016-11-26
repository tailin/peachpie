﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Pchp.Core
{
    partial class Context
    {
        /// <summary>
        /// Superglobals holder.
        /// </summary>
        protected struct Superglobals
        {
            /// <summary>
            /// Content of superglobal variables.
            /// </summary>
            public PhpArray
                globals,
                server,
                env,
                request,
                files,
                get,
                post,
                session,
                cookie;

            #region Helpers

            /// <summary>
            /// Fixes top level variable name to not contain spaces and dots (as it is in PHP);
            /// </summary>
            static string EncodeTopLevelName(string/*!*/name)
            {
                Debug.Assert(name != null);

                return name.Replace('.', '_').Replace(' ', '_');
            }

            static IPhpArray EnsureItemArray(IPhpArray array, string key)
            {
                if (string.IsNullOrEmpty(key))
                {
                    var newarr = new PhpArray();
                    array.AddValue(PhpValue.Create(newarr));
                    return newarr;
                }
                else
                {
                    return array.EnsureItemArray(new IntStringKey(key));
                }
            }

            /// <summary>
            /// Adds a variable to auto-global array.
            /// </summary>
            /// <param name="array">The array.</param>
            /// <param name="name">A unparsed name of variable.</param>
            /// <param name="value">A value to be added.</param>
            /// <param name="subname">A name of intermediate array inserted before the value.</param>
            public static void AddVariable(IPhpArray/*!*/ array, string name, string value, string subname)
            {
                Debug.Assert(array != null);
                Debug.Assert(name != null);
                Debug.Assert(value != null);

                string key;

                // current left and right square brace positions:
                int left, right;

                // checks pattern {var_name}[{key1}][{key2}]...[{keyn}] where var_name is [^[]* and keys are [^]]*:
                left = name.IndexOf('[');
                if (left > 0 && left < name.Length - 1 && (right = name.IndexOf(']', left + 1)) >= 0)
                {
                    // the variable name is a key to the "array", dots are replaced by underscores in top-level name:
                    key = EncodeTopLevelName(name.Substring(0, left));

                    // ensures that all [] operators in the chain except for the last one are applied on an array:
                    for (;;)
                    {
                        // adds a level keyed by "key":
                        array = EnsureItemArray(array, key);

                        // adds a level keyed by "subname" (once only):
                        if (subname != null)
                        {
                            array = EnsureItemArray(array, subname);
                            subname = null;
                        }

                        // next key:
                        key = name.Substring(left + 1, right - left - 1);

                        // breaks if ']' is not followed by '[':
                        left = right + 1;
                        if (left == name.Length || name[left] != '[') break;

                        // the next right brace:
                        right = name.IndexOf(']', left + 1);
                    }

                    if (string.IsNullOrEmpty(key))
                    {
                        array.AddValue(PhpValue.Create(value));
                    }
                    else
                    {
                        array.SetItemValue(new IntStringKey(key), PhpValue.Create(value));
                    }
                }
                else
                {
                    // no array pattern in variable name, "name" is a top-level key:
                    name = EncodeTopLevelName(name);

                    // inserts a subname on the next level:
                    if (subname != null)
                    {
                        EnsureItemArray(array, name).SetItemValue(new IntStringKey(subname), PhpValue.Create(value));
                    }
                    else
                    {
                        array.SetItemValue(new IntStringKey(name), PhpValue.Create(value));
                    }
                }
            }

            /// <summary>
            /// Adds variables from one auto-global array to another.
            /// </summary>
            /// <param name="dst">The target array.</param>
            /// <param name="src">The source array.</param>
            /// <remarks>Variable values are deeply copied.</remarks>
            public static void AddVariables(PhpArray/*!*/ dst, PhpArray/*!*/ src)
            {
                Debug.Assert(dst != null && src != null);

                var e = src.GetFastEnumerator();
                while (e.MoveNext())
                {
                    dst.SetItemValue(e.CurrentKey, e.CurrentValue.DeepCopy());
                }
            }

            /// <summary>
            /// Adds file variables from $_FILE array to $GLOBALS array.
            /// </summary>
            /// <param name="globals">$GLOBALS array.</param>
            /// <param name="files">$_FILES array.</param>
            internal static void AddFileVariablesToGlobals(PhpArray/*!*/ globals, PhpArray/*!*/ files)
            {
                var e = files.GetFastEnumerator();
                while (e.MoveNext())
                {
                    var file_info = e.CurrentValue.AsArray();
                    var keystr = e.CurrentKey.ToString();

                    globals[e.CurrentKey] = file_info["tmp_name"];
                    globals[keystr + "_name"] = file_info["name"];
                    globals[keystr + "_type"] = file_info["type"];
                    globals[keystr + "_size"] = file_info["size"];
                }
            }

            public static void InitializeEGPCSForWeb(ref Superglobals superglobals, string registering_order = null)
            {
                // adds EGPCS variables as globals:
                var globals = superglobals.globals;
                // adds items in the order specified by RegisteringOrder config option (overwrites existing):
                for (int i = 0; i < registering_order.Length; i++)
                {
                    switch (registering_order[i])
                    {
                        case 'E': Superglobals.AddVariables(globals, superglobals.env); break;
                        case 'G': Superglobals.AddVariables(globals, superglobals.get); break;

                        case 'P':
                            Superglobals.AddVariables(globals, superglobals.post);
                            Superglobals.AddFileVariablesToGlobals(globals, superglobals.files);
                            break;

                        case 'C': Superglobals.AddVariables(globals, superglobals.cookie); break;
                        case 'S': Superglobals.AddVariables(globals, superglobals.server); break;
                    }
                }
            }

            public static void InitializeEGPCSForConsole(ref Superglobals superglobals)
            {
                Superglobals.AddVariables(superglobals.globals, superglobals.env);
            }

            #endregion

            /// <summary>
            /// Application wide $_ENV array.
            /// </summary>
            public static PhpArray StaticEnv => static_env ?? (static_env = InitializeEnv());

            static PhpArray InitializeEnv()
            {
                var env_vars = Environment.GetEnvironmentVariables();
                var array = new PhpArray(env_vars.Count);

                foreach (DictionaryEntry entry in env_vars)
                {
                    AddVariable(array, (string)entry.Key, (string)entry.Value, null);
                }

                return array;
            }

            static PhpArray static_env;
        }

        Superglobals _superglobals;

        /// <summary>
        /// Must be called by derived constructor to initialize content of superglobal variables.
        /// </summary>
        protected void InitializeSuperglobals() => InitializeSuperglobals(ref _superglobals);

        void InitializeSuperglobals(ref Superglobals superglobals)
        {
            superglobals.env = Superglobals.StaticEnv.DeepCopy();
            superglobals.server = InitializeServerVariable();
            superglobals.request = InitializeRequestVariable();
            superglobals.get = new PhpArray();
            superglobals.post = new PhpArray();
            superglobals.files = new PhpArray();
            superglobals.session = new PhpArray();
            superglobals.cookie = new PhpArray();
            superglobals.globals = InitializeGlobals(null); // TODO: Configuration/EGPCS
        }

        /// <summary>
        /// Initializes <c>_GLOBALS</c> array.
        /// </summary>
        /// <param name="registering_order"><c>EGPCS</c> or <c>null</c> if register globals is disabled (default).</param>
        protected virtual PhpArray InitializeGlobals(string registering_order = null)
        {
            Debug.Assert(_superglobals.request != null && _superglobals.env != null && _superglobals.server != null && _superglobals.files != null);

            var globals = new PhpArray(128);

            // estimates the initial capacity of $GLOBALS array:

            // adds EGPCS variables as globals:
            if (registering_order != null)
            {
                if (IsWebApplication)
                    Superglobals.InitializeEGPCSForWeb(ref _superglobals, registering_order);
                else
                    Superglobals.InitializeEGPCSForConsole(ref _superglobals);
            }

            // adds auto-global variables (overwrites potential existing variables in $GLOBALS):
            globals["GLOBALS"] = PhpValue.Create(new PhpAlias(PhpValue.Create(globals)));   // &$_GLOBALS
            globals["_ENV"] = PhpValue.Create(_superglobals.env);
            globals["_GET"] = PhpValue.Create(_superglobals.get);
            globals["_POST"] = PhpValue.Create(_superglobals.post);
            globals["_COOKIE"] = PhpValue.Create(_superglobals.request);
            globals["_REQUEST"] = PhpValue.Create(_superglobals.globals);
            globals["_SERVER"] = PhpValue.Create(_superglobals.server);
            globals["_FILES"] = PhpValue.Create(_superglobals.files);
            globals["_SESSION"] = PhpValue.Create(_superglobals.session);
            //globals["HTTP_RAW_POST_DATA"] = HttpRawPostData;

            //// adds long arrays:
            //if (Configuration.Global.GlobalVariables.RegisterLongArrays)
            //{
            //    globals.Add("HTTP_ENV_VARS", new PhpReference(((PhpArray)Env.Value).DeepCopy()));
            //    globals.Add("HTTP_GET_VARS", new PhpReference(((PhpArray)Get.Value).DeepCopy()));
            //    globals.Add("HTTP_POST_VARS", new PhpReference(((PhpArray)Post.Value).DeepCopy()));
            //    globals.Add("HTTP_COOKIE_VARS", new PhpReference(((PhpArray)Cookie.Value).DeepCopy()));
            //    globals.Add("HTTP_SERVER_VARS", new PhpReference(((PhpArray)Server.Value).DeepCopy()));
            //    globals.Add("HTTP_POST_FILES", new PhpReference(((PhpArray)Files.Value).DeepCopy()));

            //    // both session array references the same array:
            //    globals.Add("HTTP_SESSION_VARS", Session);
            //}

            //
            return globals;
        }

        /// <summary>Initialize $_SERVER global variable.</summary>
        protected virtual PhpArray InitializeServerVariable() => new PhpArray();

        /// <summary>Initialize $_REQUEST global variable.</summary>
        protected virtual PhpArray InitializeRequestVariable() => new PhpArray();

        #region Properties

        /// <summary>
        /// Array of global variables.
        /// Cannot be <c>null</c>.
        /// </summary>
        public PhpArray Globals
        {
            get { return _superglobals.globals; }
            set
            {
                if (value == null)
                {
                    throw new ArgumentNullException();  // TODO: ErrCode
                }

                _superglobals.globals = value;
            }
        }

        /// <summary>
        /// Array of server and execution environment information.
        /// Cannot be <c>null</c>.
        /// </summary>
        public PhpArray Server
        {
            get { return _superglobals.server; }
            set
            {
                if (value == null)
                {
                    throw new ArgumentNullException();
                }

                _superglobals.server = value;
            }
        }

        /// <summary>
        /// An associative array of variables passed to the current script via the environment method.
        /// Cannot be <c>null</c>.
        /// </summary>
        public PhpArray Env
        {
            get { return _superglobals.env; }
            set
            {
                if (value == null)
                {
                    throw new ArgumentNullException();
                }

                _superglobals.env = value;
            }
        }

        /// <summary>
        /// An array that by default contains the contents of $_GET, $_POST and $_COOKIE.
        /// Cannot be <c>null</c>.
        /// </summary>
        public PhpArray Request
        {
            get { return _superglobals.request; }
            set
            {
                if (value == null)
                {
                    throw new ArgumentNullException();
                }

                _superglobals.request = value;
            }
        }

        /// <summary>
        /// An associative array of variables passed to the current script via the URL parameters.
        /// Cannot be <c>null</c>.
        /// </summary>
        public PhpArray Get
        {
            get { return _superglobals.get; }
            set
            {
                if (value == null)
                {
                    throw new ArgumentNullException();
                }

                _superglobals.get = value;
            }
        }

        /// <summary>
        /// An associative array of variables passed to the current script via the HTTP POST method.
        /// Cannot be <c>null</c>.
        /// </summary>
        public PhpArray Post
        {
            get { return _superglobals.post; }
            set
            {
                if (value == null)
                {
                    throw new ArgumentNullException();
                }

                _superglobals.post = value;
            }
        }

        /// <summary>
        /// An associative array of items uploaded to the current script via the HTTP POST method.
        /// Cannot be <c>null</c>.
        /// </summary>
        public PhpArray Files
        {
            get { return _superglobals.files; }
            set
            {
                if (value == null)
                {
                    throw new ArgumentNullException();
                }

                _superglobals.files = value;
            }
        }

        /// <summary>
        /// An associative array containing session variables available to the current script.
        /// Cannot be <c>null</c>.
        /// </summary>
        public PhpArray Session
        {
            get { return _superglobals.session; }
            set
            {
                if (value == null)
                {
                    throw new ArgumentNullException();
                }

                _superglobals.session = value;
            }
        }

        /// <summary>
        /// An associative array of variables passed to the current script via the HTTP POST method.
        /// Cannot be <c>null</c>.
        /// </summary>
        public PhpArray Cookie
        {
            get { return _superglobals.cookie; }
            set
            {
                if (value == null)
                {
                    throw new ArgumentNullException();
                }

                _superglobals.cookie = value;
            }
        }

        #endregion
    }
}
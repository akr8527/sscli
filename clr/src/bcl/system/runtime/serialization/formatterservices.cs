// ==++==
// 
//   
//    Copyright (c) 2006 Microsoft Corporation.  All rights reserved.
//   
//    The use and distribution terms for this software are contained in the file
//    named license.txt, which can be found in the root of this distribution.
//    By using this software in any fashion, you are agreeing to be bound by the
//    terms of this license.
//   
//    You must not remove this notice, or any other, from this software.
//   
// 
// ==--==
/*============================================================
**
** Class: FormatterServices
**
**
** Purpose: Provides some static methods to aid with the implementation
**          of a Formatter for Serialization.
**
**
============================================================*/
namespace System.Runtime.Serialization {
    
    using System;
    using System.Reflection;
    using System.Reflection.Cache;
    using System.Collections;
    using System.Collections.Generic;
    using System.Security;    
    using System.Security.Permissions;
    using System.Runtime.Serialization.Formatters;
    using System.Runtime.Remoting;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using StackCrawlMark = System.Threading.StackCrawlMark;
    using System.IO;
    using System.Text;
    using System.Globalization;

[System.Runtime.InteropServices.ComVisible(true)]
    public sealed class FormatterServices {
        internal static Dictionary<MemberHolder, MemberInfo[]> m_MemberInfoTable = new Dictionary<MemberHolder, MemberInfo[]>(32);
    
        private static Object s_FormatterServicesSyncObject = null;

        private static Object formatterServicesSyncObject
        {
            get
            {
                if (s_FormatterServicesSyncObject == null)
                {
                    Object o = new Object();
                    Interlocked.CompareExchange(ref s_FormatterServicesSyncObject, o, null);
                }
                return s_FormatterServicesSyncObject;
            }
        }

        private FormatterServices() {
            throw new NotSupportedException();
        }
        
        private static MemberInfo[] GetSerializableMembers(RuntimeType type) {
            // get the list of all fields
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            int countProper = 0;
            for (int i = 0;  i < fields.Length; i++) {
                if ((fields[i].Attributes & FieldAttributes.NotSerialized) == FieldAttributes.NotSerialized)
                    continue;
                countProper++;
            }
            if (countProper != fields.Length) {
                FieldInfo[] properFields = new FieldInfo[countProper];
                countProper = 0;
                for (int i = 0;  i < fields.Length; i++) {
                    if ((fields[i].Attributes & FieldAttributes.NotSerialized) == FieldAttributes.NotSerialized)
                        continue;
                    properFields[countProper] = fields[i];
                    countProper++;
                }
                return properFields;
            }
            else
                return fields;
        }

        private static bool CheckSerializable(RuntimeType type) {
            if (type.IsSerializable) {
                return true;
            }
            return false;
        }

        private static MemberInfo[] InternalGetSerializableMembers(RuntimeType type) {
            ArrayList allMembers=null;
            MemberInfo[] typeMembers;
            FieldInfo [] typeFields;
            RuntimeType parentType;

            BCLDebug.Assert(type!=null, "[GetAllSerializableMembers]type!=null");
            
            if (type.IsInterface) {
                return new MemberInfo[0];
            }

            if (!(CheckSerializable(type))) {
                    throw new SerializationException(String.Format(CultureInfo.CurrentCulture, Environment.GetResourceString("Serialization_NonSerType"), type.FullName, type.Module.Assembly.FullName));
            }
          
            //Get all of the serializable members in the class to be serialized.
            typeMembers = GetSerializableMembers(type);

            //If this class doesn't extend directly from object, walk its hierarchy and 
            //get all of the private and assembly-access fields (e.g. all fields that aren't
            //virtual) and include them in the list of things to be serialized.  
            parentType = (RuntimeType)(type.BaseType);
            if (parentType!=null && parentType!=typeof(Object)) {
                Type[] parentTypes = null;
                int parentTypeCount = 0;
                bool classNamesUnique = GetParentTypes(parentType, out parentTypes, out parentTypeCount);
                if (parentTypeCount > 0){
                    allMembers = new ArrayList();
                    for (int i = 0; i < parentTypeCount;i++){
                        parentType = (RuntimeType)parentTypes[i];
                        if (!CheckSerializable(parentType)) {
                                throw new SerializationException(String.Format(CultureInfo.CurrentCulture, Environment.GetResourceString("Serialization_NonSerType"), parentType.FullName, parentType.Module.Assembly.FullName));
                        }

                        typeFields = parentType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
                        String typeName = classNamesUnique ? parentType.Name : parentType.FullName;
                        foreach (FieldInfo field in typeFields) {
                            // Family and Assembly fields will be gathered by the type itself.
                            if (!field.IsNotSerialized) {
                                allMembers.Add(new SerializationFieldInfo((RuntimeFieldInfo)field, typeName));
                            }
                        }
                    }
                    //If we actually found any new MemberInfo's, we need to create a new MemberInfo array and
                    //copy all of the members which we've found so far into that.
                    if (allMembers!=null && allMembers.Count>0) {
                        MemberInfo[] membersTemp = new MemberInfo[allMembers.Count + typeMembers.Length];
                        Array.Copy(typeMembers, membersTemp, typeMembers.Length);
                        allMembers.CopyTo(membersTemp, typeMembers.Length);
                        typeMembers = membersTemp;
                    }
                }
            }
            return typeMembers;
        }

        static bool GetParentTypes(Type parentType, out Type[] parentTypes, out int parentTypeCount){
            //Check if there are any dup class names. Then we need to include as part of
            //typeName to prefix the Field names in SerializationFieldInfo
            /*out*/ parentTypes = null;
            /*out*/ parentTypeCount = 0;
            bool unique = true;
            for(Type t1 = parentType;t1 != typeof(object); t1 = t1.BaseType){
                if (t1.IsInterface) continue;
                string t1Name = t1.Name;
                for(int i=0;unique && i<parentTypeCount;i++){
                    string t2Name = parentTypes[i].Name;
                    if (t2Name.Length == t1Name.Length && t2Name[0] == t1Name[0] && t1Name == t2Name){
                        unique = false;
                        break;
                    }
                }
                //expand array if needed
                if (parentTypes == null || parentTypeCount == parentTypes.Length){
                    Type[] tempParentTypes = new Type[Math.Max(parentTypeCount*2, 12)];
                    if (parentTypes != null)
                        Array.Copy(parentTypes, 0, tempParentTypes, 0, parentTypeCount);
                    parentTypes = tempParentTypes;
                }
                parentTypes[parentTypeCount++] = t1;
            }
            return unique;
        }

        // Get all of the Serializable members for a particular class.  For all practical intents and
        // purposes, this is the non-transient, non-static members (fields and properties).  In order to
        // be included, properties must have both a getter and a setter.  N.B.: A class
        // which implements ISerializable or has a serialization surrogate may not use all of these members
        // (or may have additional members).
        [SecurityPermissionAttribute(SecurityAction.LinkDemand, Flags=SecurityPermissionFlag.SerializationFormatter)]
        public static MemberInfo[] GetSerializableMembers(Type type) {
            return GetSerializableMembers(type, new StreamingContext(StreamingContextStates.All));
        }

        // Get all of the Serializable Members for a particular class.  If we're not cloning, this is all
        // non-transient, non-static fields.  If we are cloning, include the transient fields as well since
        // we know that we're going to live inside of the same context.
        [SecurityPermissionAttribute(SecurityAction.LinkDemand, Flags=SecurityPermissionFlag.SerializationFormatter)]
        public static MemberInfo[] GetSerializableMembers(Type type, StreamingContext context) {
            MemberInfo[] members;
    
            if (type==null) {
                throw new ArgumentNullException("type");
            }

            if (!(type is RuntimeType)) {
                throw new SerializationException(String.Format(CultureInfo.CurrentCulture, Environment.GetResourceString("Serialization_InvalidType"), type.ToString()));
            }
    
            MemberHolder mh = new MemberHolder(type, context);
    
            //If we've already gathered the members for this type, just return them.
            if (m_MemberInfoTable.ContainsKey(mh)) {
                return m_MemberInfoTable[mh];
            }
            
            lock (formatterServicesSyncObject) {
                //If we've already gathered the members for this type, just return them.
                if (m_MemberInfoTable.ContainsKey(mh)) {
                    return m_MemberInfoTable[mh];
                }

                members = InternalGetSerializableMembers((RuntimeType)type);
            
                m_MemberInfoTable[mh] = members;
            }
    
            return members;
        }
      
        static readonly Type[] advancedTypes = new Type[]{
            typeof(System.Runtime.Remoting.ObjRef),
            typeof(System.DelegateSerializationHolder),
            typeof(System.Runtime.Remoting.IEnvoyInfo),
            typeof(System.Runtime.Remoting.Lifetime.ISponsor),
        };
  
        public static void CheckTypeSecurity(Type t,  TypeFilterLevel securityLevel) {            
            if (securityLevel == TypeFilterLevel.Low){
                for(int i=0;i<advancedTypes.Length;i++){
                    if (advancedTypes[i].IsAssignableFrom(t))
                        throw new SecurityException(String.Format(CultureInfo.CurrentCulture, Environment.GetResourceString("Serialization_TypeSecurity"), advancedTypes[i].FullName, t.FullName));
                }                  
            }
        }    
    
        // Gets a new instance of the object.  The entire object is initalized to 0 and no 
        // constructors have been run. **THIS MEANS THAT THE OBJECT MAY NOT BE IN A STATE
        // CONSISTENT WITH ITS INTERNAL REQUIREMENTS** This method should only be used for
        // deserialization when the user intends to immediately populate all fields.  This method
        // will not create an unitialized string because it is non-sensical to create an empty
        // instance of an immutable type.
        //
        [SecurityPermissionAttribute(SecurityAction.LinkDemand, Flags=SecurityPermissionFlag.SerializationFormatter)]
        public static Object GetUninitializedObject(Type type) {
            if (type==null) {
                throw new ArgumentNullException("type");
            }
    
            if (!(type is RuntimeType)) {
                throw new SerializationException(String.Format(CultureInfo.CurrentCulture, Environment.GetResourceString("Serialization_InvalidType"), type.ToString()));
            }

            return nativeGetUninitializedObject((RuntimeType)type);
        }
    
        [SecurityPermissionAttribute(SecurityAction.LinkDemand, Flags=SecurityPermissionFlag.SerializationFormatter)]
        public static Object GetSafeUninitializedObject(Type type) {
             if (type==null) {
                throw new ArgumentNullException("type");
            }
    
            if (!(type is RuntimeType)) {
                throw new SerializationException(String.Format(CultureInfo.CurrentCulture, Environment.GetResourceString("Serialization_InvalidType"), type.ToString()));
            }
            
            if (type == typeof(System.Runtime.Remoting.Messaging.ConstructionCall) || 
                type == typeof(System.Runtime.Remoting.Messaging.LogicalCallContext) ||
                type == typeof(System.Runtime.Remoting.Contexts.SynchronizationAttribute))
                 return nativeGetUninitializedObject((RuntimeType)type);                                    
                                                                                                           
            try {                            
                return nativeGetSafeUninitializedObject((RuntimeType)type);                    
            }
            catch(SecurityException e) {                
                throw new SerializationException(String.Format(CultureInfo.CurrentCulture, Environment.GetResourceString("Serialization_Security"),  type.FullName), e);
            }                                        
        }

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        private static extern Object nativeGetSafeUninitializedObject(RuntimeType type);
    
        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        private static extern Object nativeGetUninitializedObject(RuntimeType type);

        private static Binder s_binder = Type.DefaultBinder;
        internal static void SerializationSetValue(MemberInfo fi, Object target, Object value) {
            BCLDebug.Assert(fi is RuntimeFieldInfo || fi is SerializationFieldInfo, 
                            "[SerializationSetValue]fi is RuntimeFieldInfo || fi is SerializationFieldInfo.");

            RtFieldInfo rfi = fi as RtFieldInfo;
            if (rfi != null) {
                rfi.InternalSetValue(target, value, (BindingFlags)0, s_binder, null, false);
            } else {
                ((SerializationFieldInfo)fi).InternalSetValue(target, value, (BindingFlags)0, s_binder, null, false, true);
            }
        }

        // Fill in the members of obj with the data contained in data.
        // Returns the number of members populated.
        //
        [SecurityPermissionAttribute(SecurityAction.LinkDemand, Flags=SecurityPermissionFlag.SerializationFormatter)]
        public static Object PopulateObjectMembers(Object obj, MemberInfo[] members, Object[] data) {
            MemberInfo mi;
    
            BCLDebug.Trace("SER", "[PopulateObjectMembers]Enter.");
            

            if (obj==null) {
                throw new ArgumentNullException("obj");
            }

            if (members==null) {
                throw new ArgumentNullException("members");
            }

            if (data==null) {
                throw new ArgumentNullException("data");
            }

            if (members.Length!=data.Length) {
                throw new ArgumentException(Environment.GetResourceString("Argument_DataLengthDifferent"));
            }

            for (int i=0; i<members.Length; i++) {
                mi = members[i];
    
                if (mi==null) {
                    throw new ArgumentNullException("members", String.Format(CultureInfo.CurrentCulture, Environment.GetResourceString("ArgumentNull_NullMember"), i));
                }
    
    
                //If we find an empty, it means that the value was never set during deserialization.
                //This is either a forward reference or a null.  In either case, this may break some of the
                //invariants mantained by the setter, so we'll do nothing with it for right now.
                if (data[i]!=null) {
                    if (mi.MemberType==MemberTypes.Field) {
                        SerializationSetValue(mi, obj, data[i]);
                    } else {
                        throw new SerializationException(Environment.GetResourceString("Serialization_UnknownMemberInfo"));
                    }

                    BCLDebug.Trace("SER", "[PopulateObjectMembers]\tType:", obj.GetType(), "\tMember:", 
                                   members[i].Name, " with member type: ", ((FieldInfo)members[i]).FieldType);
                }
                //Console.WriteLine("X");
            }
            
            BCLDebug.Trace("SER", "[PopulateObjectMembers]Leave.");

            return obj;
        }
    
        // Extracts the data from obj.  members is the array of members which we wish to
        // extract (must be FieldInfos or PropertyInfos).  For each supplied member, extract the matching value and
        // return it in a Object[] of the same size.
        //
        [SecurityPermissionAttribute(SecurityAction.LinkDemand, Flags=SecurityPermissionFlag.SerializationFormatter)]
        public static Object[] GetObjectData(Object obj, MemberInfo[] members) {
    
            if (obj==null) {
                throw new ArgumentNullException("obj");
            }
    
            if (members==null) {
                throw new ArgumentNullException("members");
            }
            
            int numberOfMembers = members.Length;
    
            Object[] data = new Object[numberOfMembers];
            MemberInfo mi;
    
            for (int i=0; i<numberOfMembers; i++) {
                mi=members[i];
    
                if (mi==null) {
                    throw new ArgumentNullException("members", String.Format(CultureInfo.CurrentCulture, Environment.GetResourceString("ArgumentNull_NullMember"), i));
                }
    
                if (mi.MemberType==MemberTypes.Field) {
                    BCLDebug.Assert(mi is RuntimeFieldInfo || mi is SerializationFieldInfo,
                                    "[FormatterServices.GetObjectData]mi is RuntimeFieldInfo || mi is SerializationFieldInfo.");

                    RtFieldInfo rfi = mi as RtFieldInfo;
                    if (rfi != null) {
                        data[i] = rfi.InternalGetValue(obj, false);
                    } else {
                        data[i] = ((SerializationFieldInfo)mi).InternalGetValue(obj, false);
                    }
                } else {
                    throw new SerializationException(Environment.GetResourceString("Serialization_UnknownMemberInfo"));
                }
            }
    
            return data;
        }

        
        /*=============================GetTypeFromAssembly==============================
        **Action:
        **Returns:
        **Arguments:
        **Exceptions:
        ==============================================================================*/
        [SecurityPermissionAttribute(SecurityAction.LinkDemand, Flags=SecurityPermissionFlag.SerializationFormatter)]
        public static Type GetTypeFromAssembly(Assembly assem, String name) {
            if (assem==null)
                throw new ArgumentNullException("assem");
            return assem.GetType(name, false, false);
        }
    
        /*============================LoadAssemblyFromString============================
        **Action: Loads an assembly from a given string.  The current assembly loading story
        **        is quite confusing.  If the assembly is in the fusion cache, we can load it
        **        using the stringized-name which we transmitted over the wire.  If that fails,
        **        we try for a lookup of the assembly using the simple name which is the first
        **        part of the assembly name.  If we can't find it that way, we'll return null
        **        as our failure result.
        **Returns: The loaded assembly or null if it can't be found.
        **Arguments: assemblyName -- The stringized assembly name.
        **Exceptions: None
        ==============================================================================*/
        internal static Assembly LoadAssemblyFromString(String assemblyName) {
            //
            // Try using the stringized assembly name to load from the fusion cache.
            //
            BCLDebug.Trace("SER", "[LoadAssemblyFromString]Looking for assembly: ", assemblyName);
            Assembly found = Assembly.Load(assemblyName);
            return found;
        }
        internal static Assembly LoadAssemblyFromStringNoThrow(String assemblyName) {
            try {
                return LoadAssemblyFromString(assemblyName);
            }
            catch (Exception e){
                BCLDebug.Trace("SER", "[LoadAssemblyFromString]", e.ToString());
            }
            return null;
        }
    }
}





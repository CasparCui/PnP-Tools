﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Security;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Online.SharePoint.TenantAdministration;
using Microsoft.SharePoint.Client;
using SharePoint.AccessApp.Scanner.Framework.TimerJobs.Enums;
using SharePoint.AccessApp.Scanner.Framework.TimerJobs.Utilities;
using System.Security.Cryptography.X509Certificates;
using SharePoint.AccessApp.Scanner.Utilities;

namespace SharePoint.AccessApp.Scanner.Framework.TimerJobs
{
    #region Delegates
    /// <summary>
    /// TimerJobRun delegate
    /// </summary>
    /// <param name="sender">calling object instance</param>
    /// <param name="e">TimerJobRunEventArgs event arguments instance</param>
    public delegate void TimerJobRunHandler(object sender, TimerJobRunEventArgs e);
    #endregion

    /// <summary>
    /// Abstract base class for creating timer jobs (background processes) that operate against SharePoint sites. These timer jobs 
    /// are designed to use the CSOM API and thus can run on any server that can communicate with SharePoint.
    /// </summary>
    public abstract class TimerJob
    {
        #region Private Variables
        // Timerjob information
        private string name;
        private string version;
        private bool isRunning = false;
        private string configurationData;
        // property management information
        private bool manageState = false;
        // Authentication related variables
        private ConcurrentDictionary<string, AuthenticationManager> authenticationManagers;
        private AuthenticationType authenticationType;
        private string username;
        private SecureString password;
        private string domain;
        private string realm;
        private string clientId;
        private string clientSecret;

        private int sharePointVersion = 16;
        private string enumerationUser;
        private SecureString enumerationPassword;
        private string enumerationDomain;
        private string tenantAdminSite;
        // Site scope variables
        private List<string> requestedSites;
        private List<string> sitesToProcess;
        private bool expandSubSites = false;
        // Threading
        private static int numberOfThreadsNotYetCompleted;
        private static ManualResetEvent doneEvent;
        private bool useThreading = true;
        private int maximumThreads = 5;
        // Additions for scanner
        private bool excludeOD4B = false;
        #endregion

        #region Events
        /// <summary>
        /// TimerJobRun event
        /// </summary>
        public event TimerJobRunHandler TimerJobRun;
        #endregion

        #region Constructor
        /// <summary>
        /// Simpliefied constructor for timer job, version is always set to "1.0"
        /// </summary>
        /// <param name="name">Name of the timer job</param>
        public TimerJob(string name)
            : this(name, "1.0")
        {
        }

        public TimerJob(string name, string version)
            : this(name, version, "")
        {
        }

        /// <summary>
        /// Default constructor for timer job
        /// </summary>
        /// <param name="name">Name of the timer job</param>
        /// <param name="version">Version of the timer job</param>
        /// <param name="configurationData"></param>
        public TimerJob(string name, string version, string configurationData)
        {
            this.name = name;
            this.version = version;
            this.requestedSites = new List<string>(10);
            this.sharePointVersion = GetSharePointVersion();
            this.configurationData = configurationData;

            // Default authentication model will be Office365
            this.authenticationType = AuthenticationType.Office365;
            this.authenticationManagers = new ConcurrentDictionary<string, AuthenticationManager>();
        }
        #endregion

        #region Job information & state management
        /// <summary>
        /// Gets the name of this timer job
        /// </summary>
        public string Name
        {
            get
            {
                return this.name;
            }
        }

        /// <summary>
        /// Gets the version of this timer job
        /// </summary>
        public string Version
        {
            get
            {
                return this.version;
            }
        }

        /// <summary>
        /// Gets or sets additional timer job configuration data
        /// </summary>
        public string ConfigurationData
        {
            get
            {
                return this.configurationData;
            }
            set
            {
                this.configurationData = value;
            }
        }

        /// <summary>
        /// Gets and sets the state management value: when true the timer job will automatically handle state by 
        /// storing a json serialized class as a web property bag entry. Default value is false
        /// </summary>
        public bool ManageState
        {
            get
            {
                return this.manageState;
            }
            set
            {
                this.manageState = value;
            }
        }

        /// <summary>
        /// Is this timer job running?
        /// </summary>
        public bool IsRunning
        {
            get
            {
                return this.isRunning;
            }
        }

        /// <summary>
        /// Can this timer job use multiple threads. Defaults to true
        /// </summary>
        public bool UseThreading
        {
            get
            {
                return this.useThreading;
            }
            set
            {
                this.useThreading = value;
            }
        }

        /// <summary>
        /// How many threads can be used by this timer job. Default value is 5.
        /// </summary>
        public int MaximumThreads
        {
            get
            {
                return this.maximumThreads;
            }
            set
            {
                if (value > 100)
                {
                    throw new ArgumentException("No more than 100 threads are allowed");
                }

                if (value == 1)
                {
                    throw new ArgumentException("If you only need 1 thread then turn off threading");
                }
                else if (value < 1)
                {
                    throw new ArgumentException("Number of threads must be bigger than 0");
                }

                this.maximumThreads = value;
            }
        }
        #endregion

        #region Run job
        /// <summary>   
        /// Triggers the timer job to start running
        /// </summary>
        public void Run()
        {
            try
            {
                //mark the job as running
                this.isRunning = true;

                // This method call doesn't do anything but allows the inheriting task to override the passed list of requested sites
                this.requestedSites = UpdateAddedSites(requestedSites);

                if (String.IsNullOrEmpty(this.realm) && this.authenticationType == AuthenticationType.AppOnly && requestedSites.Count > 0)
                {
                    this.realm = TokenHelper.GetRealmFromTargetUrl(new Uri(GetTopLevelSite(requestedSites[0].Replace("*", ""))));
                }

                // Prepare the list of sites to process. This will resolve the wildcard site Url's to a list of actual Url's
                this.sitesToProcess = ResolveAddedSites(this.requestedSites);

                // No sites to process...we're done
                if (this.sitesToProcess.Count == 0)
                {
                    // Job ended, so set isrunning accordingly
                    this.isRunning = false;
                    return;
                }

                // We're using multiple threads, the default option
                if (useThreading)
                {
                    // Divide the workload in batches based on the maximum number of threads that we want
                    List<List<string>> batchWork = CreateWorkBatches();

                    // Determine the number of threads we'll spin off. Will be less or equal to the set maximum number of threads
                    numberOfThreadsNotYetCompleted = batchWork.Count;
                    // Prepare the reset event for indicating thread completion
                    doneEvent = new ManualResetEvent(false);

                    // execute an thread per batch
                    foreach (List<string> batch in batchWork)
                    {
                        // add thread to queue 
                        ThreadPool.QueueUserWorkItem(o => DoWorkBatch(batch));
                    }

                    // Wait for all threads to finish
                    doneEvent.WaitOne();
                }
                else
                {
                    // No threading, just execute an event per site
                    foreach (string site in this.sitesToProcess)
                    {
                        DoWork(site);
                    }
                }
            }
            finally
            {
                // Job ended, so set isrunning accordingly
                this.isRunning = false;
            }
        }

        /// <summary>
        /// Processes the amount of work that will be done by one thread
        /// </summary>
        /// <param name="sites">Batch of sites that the thread will need to process</param>
        private void DoWorkBatch(List<string> sites)
        {
            try
            {
                // Call our work routine per site in the passed batch of sites
                foreach (string site in sites)
                {
                    DoWork(site);
                }
            }
            finally
            {
                // Decrement counter in a thread safe manner
                if (Interlocked.Decrement(ref numberOfThreadsNotYetCompleted) == 0)
                {
                    // we're done, all threads have ended, signal that this was the last thread that ended
                    doneEvent.Set();
                }
            }
        }

        /// <summary>
        /// Processes the amount of work that will be done for a single site/web
        /// </summary>
        /// <param name="site">Url of the site to process</param>
        private void DoWork(string site)
        {
            // Get the root site of the passed site
            string rootSite = GetRootSite(site);

            // Instantiate the needed ClientContext objects
            ClientContext ccWeb = CreateClientContext(site);
            ClientContext ccSite = null;

            if (rootSite.Equals(site, StringComparison.InvariantCultureIgnoreCase))
            {
                ccSite = ccWeb;
            }
            else
            {
                ccSite = CreateClientContext(rootSite);
            }

#if !ONPREMISES
            // Instantiate ClientContext against tenant admin site, this is needed to operate using the Tenant API
            string tenantAdminSiteUrl = tenantAdminSite;
            if (string.IsNullOrEmpty(tenantAdminSiteUrl))
            {
                tenantAdminSiteUrl = GetTenantAdminSite(site);
            }
            ClientContext ccTenant = CreateClientContext(tenantAdminSiteUrl);
#else
            // No easy way to detect tenant admin site in on-premises, so uses has to specify it
            ClientContext ccTenant = null;
            if (!String.IsNullOrEmpty(tenantAdminSite))
            {
                ccTenant = CreateClientContext(tenantAdminSite);
            }
#endif

            // Prepare the timerjob callback event arguments
            TimerJobRunEventArgs e = new TimerJobRunEventArgs(site, ccSite, ccWeb, ccTenant, null, null, "", new Dictionary<string, string>(), this.ConfigurationData);

            // Trigger the event to fire, but only when there's an event handler connected
            if (TimerJobRun != null)
            {
                OnTimerJobRun(e);
            }
        }

        /// <summary>
        /// Triggers the event to fire and deals with all the pre/post processing needed to automatically manage state
        /// </summary>
        /// <param name="e">TimerJobRunEventArgs event arguments class that will be passed to the event handler</param>
        private void OnTimerJobRun(TimerJobRunEventArgs e)
        {
            try
            {
                // Copy for thread safety?
                TimerJobRunHandler timerJobRunHandlerThreadCopy = TimerJobRun;
                if (timerJobRunHandlerThreadCopy != null)
                {
                    PropertyValues props = null;
                    JavaScriptSerializer s = null;

                    // if state is managed then the state value is stored in a property named "<timerjobname>_Properties"
                    string propertyKey = String.Format("{0}_Properties", NormalizedTimerJobName(this.name));

                    // read the properties from the web property bag
                    if (this.manageState)
                    {
                        props = e.WebClientContext.Web.AllProperties;
                        e.WebClientContext.Load(props);
                        e.WebClientContext.ExecuteQueryRetry();

                        s = new JavaScriptSerializer();

                        // we've found previously stored state, so this is not the first timer job run
                        if (props.FieldValues.ContainsKey(propertyKey))
                        {
                            string timerJobProps = props.FieldValues[propertyKey].ToString();

                            // We should have a value, but you never know...
                            if (!string.IsNullOrEmpty(timerJobProps))
                            {
                                // Deserialize the json string into a TimerJobRun class instance
                                TimerJobRun timerJobRunProperties = s.Deserialize<TimerJobRun>(timerJobProps);

                                // Pass the state information as part of the event arguments
                                if (timerJobRunProperties != null)
                                {
                                    e.PreviousRun = timerJobRunProperties.PreviousRun;
                                    e.PreviousRunSuccessful = timerJobRunProperties.PreviousRunSuccessful;
                                    e.PreviousRunVersion = timerJobRunProperties.PreviousRunVersion;
                                    e.Properties = timerJobRunProperties.Properties;
                                }
                            }
                        }
                    }

                    // trigger the event
                    timerJobRunHandlerThreadCopy(this, e);

                    // Update and store the properties to the web property bag
                    if (this.manageState)
                    {
                        // Retrieve the values of the event arguments and complete them with defaults
                        TimerJobRun timerJobRunProperties = new TimerJobRun()
                        {
                            PreviousRun = DateTime.Now,
                            PreviousRunSuccessful = e.CurrentRunSuccessful,
                            PreviousRunVersion = this.version,
                            Properties = e.Properties,
                        };

                        // Serialize to json string
                        string timerJobProps = s.Serialize(timerJobRunProperties);

                        props = e.WebClientContext.Web.AllProperties;

                        // Get the value, if the web properties are already loaded
                        if (props.FieldValues.Count > 0)
                        {
                            props[propertyKey] = timerJobProps;
                        }
                        else
                        {
                            // Load the web properties
                            e.WebClientContext.Load(props);
                            e.WebClientContext.ExecuteQueryRetry();

                            props[propertyKey] = timerJobProps;
                        }

                        // Persist the web property bag entries
                        e.WebClientContext.Web.Update();
                        e.WebClientContext.ExecuteQueryRetry();
                    }

                }
            }
            catch (Exception ex)
            {
                // Catch error in this case as we don't want to the whole program to terminate if one single site operation fails
            }
        }

        /// <summary>
        /// Creates batches of sites to process. Batch size is based on max number of threads
        /// </summary>
        /// <returns>List of Lists holding the work batches</returns>
        private List<List<string>> CreateWorkBatches()
        {
            // How many batches do we need, can't have more batches then sites to process
            int numberOfBatches = Math.Min(this.sitesToProcess.Count, this.maximumThreads);
            // Size of batch
            int batchCount = (this.sitesToProcess.Count / numberOfBatches);
            // Increase batch size by 1 to avoid the last batch being overloaded, rahter spread out over all batches and make the last batch smaller
            if (this.sitesToProcess.Count % numberOfBatches != 0)
            {
                batchCount++;
            }

            // Initialize batching variables
            List<List<string>> batches = new List<List<string>>(numberOfBatches);
            List<string> sitesBatch = new List<string>(batchCount);
            int batchCounter = 0;
            int batchesAdded = 1;

            for (int i = 0; i < this.sitesToProcess.Count; i++)
            {
                sitesBatch.Add(this.sitesToProcess[i]);
                batchCounter++;

                // we've filled one batch, let's create a new one
                if (batchCounter == batchCount && batchesAdded < numberOfBatches)
                {
                    batches.Add(sitesBatch);
                    batchesAdded++;
                    sitesBatch = new List<string>(batchCount);
                    batchCounter = 0;
                }
            }

            // add the last batch to the list of batches
            if (sitesBatch.Count > 0)
            {
                batches.Add(sitesBatch);
            }

            return batches;
        }
        #endregion

        #region Authentication methods and attributes

        /// <summary>
        /// Gets the authentication type that the timer job will use. This will be set as part 
        /// of the UseOffice365Authentication and UseNetworkCredentialsAuthentication methods
        /// </summary>
        public AuthenticationType AuthenticationType
        {
            get
            {
                return this.authenticationType;
            }
        }

        /// <summary>
        /// Gets or sets the SharePoint version. Default value is detected based on the laoded CSOM assembly version, but can be overriden
        /// in case you want to for example use v16 assemblies in v15 (on-premises)
        /// </summary>
        public int SharePointVersion
        {
            get
            {
                return this.sharePointVersion;
            }
            set
            {
                if (value < 15 || value > 16)
                {
                    throw new ArgumentException("Unknown SharePoint version");
                }

                this.sharePointVersion = value;
            }
        }

        /// <summary>
        /// Realm will be automatically defined, but there's an option to manually specify it which may 
        /// be needed when did an override of ResolveAddedSites and specify your sites.
        /// </summary>
        public string Realm
        {
            get
            {
                return this.realm;
            }
            set
            {
                this.realm = value;
            }
        }

        /// <summary>
        /// Option to specify the tenant admin site. For MT this typically is not needed since we can detect the tenant admin site, but for on premises and DvNext this is needed
        /// </summary>
        public string TenantAdminSite
        {
            get
            {
                return this.tenantAdminSite;
            }
            set
            {
                this.tenantAdminSite = value;
            }
        }

        /// <summary>
        /// Prepares the timerjob to operate against Office 365 with user and password credentials. Sets AuthenticationType 
        /// to AuthenticationType.Office365
        /// </summary>
        /// <param name="userUPN"></param>
        /// <param name="password">Password of the user that will be used to operate the timer job work</param>
        public void UseOffice365Authentication(string userUPN, string password)
        {
            if (String.IsNullOrEmpty(password))
            {
                throw new ArgumentNullException("password");
            }

            UseOffice365Authentication(userUPN, EncryptionUtility.ToSecureString(password));
        }

        /// <summary>
        /// Prepares the timerjob to operate against Office 365 with user and password credentials. Sets AuthenticationType 
        /// to AuthenticationType.Office365
        /// </summary>
        /// <param name="userUPN"></param>
        /// <param name="password">Password of the user that will be used to operate the timer job work</param>
        public void UseOffice365Authentication(string userUPN, SecureString password)
        {
            if (String.IsNullOrEmpty(userUPN))
            {
                throw new ArgumentNullException("userName");
            }

            if (password == null || password.Length == 0)
            {
                throw new ArgumentNullException("password");
            }

            this.authenticationType = AuthenticationType.Office365;
            this.username = userUPN;
            this.password = password;
        }

        /// <summary>
        /// Prepares the timerjob to operate against Office 365 with user and password credentials which are retrieved via 
        /// the windows Credential Manager. Also sets AuthenticationType to AuthenticationType.Office365
        /// </summary>
        /// <param name="credentialName">Name of the credential manager registration</param>
        public void UseOffice365Authentication(string credentialName)
        {
            if (String.IsNullOrEmpty(credentialName))
            {
                throw new ArgumentNullException("credentialName");
            }

            NetworkCredential cred = CredentialManager.GetCredential(credentialName);

            SecureString securePassword = null;
            if (cred != null)
            {
                securePassword = cred.SecurePassword;
            }

            if (cred != null && !String.IsNullOrEmpty(cred.UserName) && securePassword != null && securePassword.Length != 0)
            {
                UseOffice365Authentication(cred.UserName, securePassword);
            }
        }

        /// <summary>
        /// Prepares the timerjob to operate against SharePoint on-premises with user name password credentials. Sets AuthenticationType 
        /// to AuthenticationType.NetworkCredentials
        /// </summary>
        /// <param name="samAccountName">samAccontName of the windows user</param>
        /// <param name="password">Password of the windows user</param>
        /// <param name="domain">NT domain of the windows user</param>
        public void UseNetworkCredentialsAuthentication(string samAccountName, string password, string domain)
        {
            if (String.IsNullOrEmpty(password))
            {
                throw new ArgumentNullException("password");
            }

            UseNetworkCredentialsAuthentication(samAccountName, EncryptionUtility.ToSecureString(password), domain);
        }

        /// <summary>
        /// Prepares the timerjob to operate against SharePoint on-premises with user name password credentials. Sets AuthenticationType 
        /// to AuthenticationType.NetworkCredentials
        /// </summary>
        /// <param name="samAccountName">samAccontName of the windows user</param>
        /// <param name="password">Password of the windows user</param>
        /// <param name="domain">NT domain of the windows user</param>
        public void UseNetworkCredentialsAuthentication(string samAccountName, SecureString password, string domain)
        {
            if (String.IsNullOrEmpty(samAccountName))
            {
                throw new ArgumentNullException("userName");
            }

            if (password == null || password.Length == 0)
            {
                throw new ArgumentNullException("password");
            }

            if (String.IsNullOrEmpty(domain))
            {
                throw new ArgumentNullException("domain");
            }

            this.authenticationType = AuthenticationType.NetworkCredentials;
            this.username = samAccountName;
            this.password = password;
            this.domain = domain;

        }

        /// <summary>
        /// Prepares the timerjob to operate against SharePoint on-premises with user name password  credentials which are retrieved via 
        /// the windows Credential Manager. Sets AuthenticationType to AuthenticationType.NetworkCredentials
        /// </summary>
        /// <param name="credentialName">Name of the credential manager registration</param>
        public void UseNetworkCredentialsAuthentication(string credentialName)
        {
            if (String.IsNullOrEmpty(credentialName))
            {
                throw new ArgumentNullException("credentialName");
            }

            NetworkCredential cred = CredentialManager.GetCredential(credentialName);

            if (!String.IsNullOrEmpty(cred.UserName))
            {
                string[] parts = cred.UserName.Split(new string[] { "\\" }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    cred.UserName = parts[1];
                    cred.Domain = parts[0];
                }
            }

            SecureString securePassword = null;
            if (cred != null)
            {
                securePassword = cred.SecurePassword;
            } 

            if (cred != null && !String.IsNullOrEmpty(cred.UserName) && securePassword != null && securePassword.Length != 0 && !String.IsNullOrEmpty(cred.Domain))
            {
                UseNetworkCredentialsAuthentication(cred.UserName, securePassword, cred.Domain);
            }

        }

        /// <summary>
        /// Prepares the timerjob to operate against SharePoint on-premises with app-only credentials. Sets AuthenticationType 
        /// to AuthenticationType.AppOnly
        /// </summary>
        /// <param name="clientId">Client ID of the app</param>
        /// <param name="clientSecret">Client Secret of the app</param>
        public void UseAppOnlyAuthentication(string clientId, string clientSecret)
        {
            if (String.IsNullOrEmpty(clientId))
            {
                throw new ArgumentNullException("clientId");
            }

            if (String.IsNullOrEmpty(clientSecret))
            {
                throw new ArgumentNullException("clientSecret");
            }

            this.authenticationType = AuthenticationType.AppOnly;
            this.clientId = clientId;
            this.clientSecret = clientSecret;

        }

        /// <summary>
        /// Takes over the settings from the passed timer job. Is useful when you run multiple jobs in a row or chain 
        /// job execution. Settings that are taken over are all the authentication, enumeration settings and SharePointVersion
        /// </summary>
        /// <param name="job"></param>
        public void Clone(TimerJob job)
        {
            this.username = job.username;
            this.password = job.password;
            this.domain = job.domain;
            this.enumerationUser = job.enumerationUser;
            this.enumerationPassword = job.enumerationPassword;
            this.enumerationDomain = job.enumerationDomain;
            this.authenticationType = job.authenticationType;
            this.realm = job.realm;
            this.clientId = job.clientId;
            this.clientSecret = job.clientSecret;
            this.sharePointVersion = job.sharePointVersion;
        }

        /// <summary>
        /// Get an AuthenticationManager instance per host Url. Needed to make this work properly, else we're getting access denied 
        /// because of Invalid audience Uri
        /// </summary>
        /// <param name="url">Url of the site</param>
        /// <returns>An instantiated AuthenticationManager</returns>
        private AuthenticationManager GetAuthenticationManager(string url)
        {
            // drop the wild card if still there
            Uri uri = new Uri(url.Replace("*", ""));

            if (this.authenticationManagers.ContainsKey(uri.Host))
            {
                return this.authenticationManagers[uri.Host];
            }
            else
            {
                AuthenticationManager am = new AuthenticationManager();
                this.authenticationManagers.TryAdd(uri.Host, am);
                return am;
            }
        }
        #endregion

        #region Site scope methods and attributes
        /// <summary>
        /// Does the timerjob need to fire as well for every sub site in the site?
        /// </summary>
        public bool ExpandSubSites
        {
            get
            {
                return this.expandSubSites;
            }
            set
            {
                this.expandSubSites = value;
            }
        }

        /// <summary>
        /// Returns the user account used for enumaration. Enumeration is done using search and the search API requires a user context
        /// </summary>
        private string EnumerationUser
        {
            get
            {
                if (!String.IsNullOrEmpty(this.enumerationUser))
                {
                    return this.enumerationUser;
                }
                else if (!String.IsNullOrEmpty(this.username))
                {
                    return this.username;
                }
                else
                {
                    throw new Exception("Please specify an enumeration user");
                }
            }
        }

        /// <summary>
        /// Returns the password of the user account used for enumaration. Enumeration is done using search and the search API requires a user context
        /// </summary>
        private SecureString EnumerationPassword
        {
            get
            {
                if (this.enumerationPassword != null && this.enumerationPassword.Length > 0)
                {
                    return this.enumerationPassword;
                }
                else if (this.password != null && this.password.Length > 0)
                {
                    return this.password;
                }
                else
                {
                    throw new Exception("Please specify an enumeration user password");
                }
            }
        }

        /// <summary>
        /// Returns the domain of the user account used for enumaration. Enumeration is done using search and the search API requires a user context
        /// </summary>
        private string EnumerationDomain
        {
            get
            {
                if (!String.IsNullOrEmpty(this.enumerationDomain))
                {
                    return this.enumerationDomain;
                }
                else if (!String.IsNullOrEmpty(this.domain))
                {
                    return this.domain;
                }
                else
                {
                    throw new Exception("Please specify an enumeration user domain");
                }
            }
        }

        public bool ExcludeOD4B
        {
            get
            {
                return this.excludeOD4B;
            }
            set
            {
                this.excludeOD4B = value;
            }
        }

        /// <summary>
        /// Provides the timer job with the enumeration credentials. For Office 365 username and password is sufficient
        /// </summary>
        /// <param name="userUPN"></param>
        /// <param name="password">Password of the enumeration user</param>
        public void SetEnumerationCredentials(string userUPN, string password)
        {
            if (String.IsNullOrEmpty(password))
            {
                throw new ArgumentNullException("password");
            }

            SetEnumerationCredentials(userUPN, EncryptionUtility.ToSecureString(password));
        }

        /// <summary>
        /// Provides the timer job with the enumeration credentials. For Office 365 username and password is sufficient
        /// </summary>
        /// <param name="userUPN"></param>
        /// <param name="password">Password of the enumeration user</param>
        public void SetEnumerationCredentials(string userUPN, SecureString password)
        {
            if (String.IsNullOrEmpty(userUPN))
            {
                throw new ArgumentNullException("userUPN");
            }

            if (password == null || password.Length == 0)
            {
                throw new ArgumentNullException("password");
            }

            this.enumerationUser = userUPN;
            this.enumerationPassword = password;
        }

        /// <summary>
        /// Provides the timer job with the enumeration credentials. For SharePoint on-premises username, password and domain are needed
        /// </summary>
        /// <param name="samAccountName">UPN of the enumeration user</param>
        /// <param name="password">Password of the enumeration user</param>
        /// <param name="domain">Domain of the enumeration user</param>
        public void SetEnumerationCredentials(string samAccountName, string password, string domain)
        {
            if (String.IsNullOrEmpty(password))
            {
                throw new ArgumentNullException("password");
            }

            SetEnumerationCredentials(samAccountName, EncryptionUtility.ToSecureString(password), domain);
        }

        /// <summary>
        /// Provides the timer job with the enumeration credentials. For SharePoint on-premises username, password and domain are needed
        /// </summary>
        /// <param name="samAccountName">Account name of the enumeration user</param>
        /// <param name="password">Password of the enumeration user</param>
        /// <param name="domain">Domain of the enumeration user</param>
        public void SetEnumerationCredentials(string samAccountName, SecureString password, string domain)
        {
            if (String.IsNullOrEmpty(samAccountName))
            {
                throw new ArgumentNullException("samAccountName");
            }

            if (password == null || password.Length == 0)
            {
                throw new ArgumentNullException("password");
            }

            if (String.IsNullOrEmpty(domain))
            {
                throw new ArgumentNullException("domain");
            }

            this.enumerationUser = samAccountName;
            this.enumerationPassword = password;
            this.enumerationDomain = domain;
        }

        /// <summary>
        /// Provides the timer job with the enumeration credentials. For SharePoint on-premises username, password and domain are needed
        /// </summary>
        /// <param name="credentialName">Name of the credential manager registration</param>
        public void SetEnumerationCredentials(string credentialName)
        {
            if (String.IsNullOrEmpty(credentialName))
            {
                throw new ArgumentNullException("credentialName");
            }

            NetworkCredential cred = CredentialManager.GetCredential(credentialName);

            SecureString securePassword = null;
            if (cred != null)
            {
                securePassword = cred.SecurePassword;
            }

            if (cred != null && !String.IsNullOrEmpty(cred.UserName) && securePassword != null && securePassword.Length != 0)
            {

                if (!String.IsNullOrEmpty(cred.UserName))
                {
                    string[] parts = cred.UserName.Split(new string[] { "\\" }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2)
                    {
                        cred.UserName = parts[1];
                        cred.Domain = parts[0];
                    }
                }


                if (String.IsNullOrEmpty(cred.Domain))
                {
                    SetEnumerationCredentials(cred.UserName, securePassword);
                }
                else
                {
                    SetEnumerationCredentials(cred.UserName, securePassword, cred.Domain);
                }
            }
            else
            {
                throw new Exception(String.Format("Credentials for {0} could not retrieved", credentialName));
            }
        }

        /// <summary>
        /// Adds a site Url or wildcard site Url to the collection of sites that the timer job will process
        /// </summary>
        /// <param name="site">Site Url or wildcard site Url to be processed by the timer job</param>
        public void AddSite(string site)
        {
            if (String.IsNullOrEmpty(site))
            {
                throw new ArgumentNullException("site");
            }

            site = site.ToLower();

            if (!site.Contains("*"))
            {
                if (!IsValidUrl(site))
                {
                    throw new ArgumentException(string.Format("Site to add {0} was not valid", site), "site");
                }
            }

            if (!requestedSites.Contains(site))
            {
                this.requestedSites.Add(site);
            }
        }

        /// <summary>
        /// Clears the list of added site Url's and/or wildcard site Url's
        /// </summary>
        public void ClearAddedSites()
        {
            this.requestedSites.Clear();
        }

        /// <summary>
        /// Virtual method that can be overriden to allow the timer job itself to control the list of sites to operate against.
        /// Scenario is for example timer job that reads this data from a database instead of being fed by the calling program
        /// </summary>
        /// <param name="addedSites">List of added site Url's and/or wildcard site Url's</param>
        /// <returns>List of added site Url's and/or wildcard site Url's</returns>
        public virtual List<string> UpdateAddedSites(List<string> addedSites)
        {
            // Default behavior is just pass back the given list
            return addedSites;
        }

        /// <summary>
        /// Virtual method that can be overriden to control the list of resolved sites
        /// </summary>
        /// <param name="addedSites">List of added site Url's and/or wildcard site Url's</param>
        /// <returns>List of resolved sites</returns>
        public virtual List<string> ResolveAddedSites(List<string> addedSites)
        {
            List<string> resolvedSites = new List<string>();

            // Step 1: obtain the list of all site collections
            foreach (string site in this.requestedSites)
            {
                if (site.Contains("*"))
                {
                    // get the actual sites matching to the wildcard site Url
                    ResolveSite(site, resolvedSites);
                }
                else
                {
                    resolvedSites.Add(site);
                }
            }

            // Clear the used authentication managers
            this.authenticationManagers.Clear();

            // Step 2 (optional): If the job wants to run at sub site level then we'll need to resolve all sub sites
            if (expandSubSites)
            {
                List<string> resolvedSitesAndSubSites = new List<string>();

                // Prefered option is to use threading to increase the list resolving speed
                if (useThreading)
                {
                    // Split the sites to resolve in batches
                    List<List<string>> expandBatches = CreateExpandBatches(resolvedSites);

                    // Determine the number of threads we'll spin off. Will be less or equal to the maximum number of threads
                    numberOfThreadsNotYetCompleted = expandBatches.Count;
                    // Prepare the reset event for indicating thread completion
                    doneEvent = new ManualResetEvent(false);

                    foreach (List<string> expandBatch in expandBatches)
                    {
                        // Launch a thread per batch of sites to expand
                        ThreadPool.QueueUserWorkItem(o => DoExpandBatch(expandBatch, resolvedSitesAndSubSites));
                    }

                    // Wait for all threads to finish
                    doneEvent.WaitOne();
                }
                else
                {
                    // When no threading just sequentially expand the sub sites for each site collection
                    for (int i = 0; i < resolvedSites.Count; i++)
                    {
                        ExpandSite(resolvedSitesAndSubSites, resolvedSites[i]);
                    }
                }

                return resolvedSitesAndSubSites;
            }
            else
            {
                // no sub site resolving was needed, so just return the original list of resolved sites
                return resolvedSites;
            }
        }

        /// <summary>
        /// Processes one bach of sites to expand, whcih is the workload of one thread
        /// </summary>
        /// <param name="sites">Batch of sites to expand</param>
        /// <param name="resolvedSitesAndSubSites">List holding the expanded sites</param>
        private void DoExpandBatch(List<string> sites, List<string> resolvedSitesAndSubSites)
        {
            try
            {
                foreach (string site in sites)
                {
                    // perform the site expansion for a single site collection
                    ExpandSite(resolvedSitesAndSubSites, site);
                }
            }
            finally
            {
                // Decrement counter in a thread safe manner
                if (Interlocked.Decrement(ref numberOfThreadsNotYetCompleted) == 0)
                {
                    // we're done, all threads have ended, signal that this was the last thread that ended
                    doneEvent.Set();
                }
            }
        }

        /// <summary>
        /// Creates batches of sites to expand
        /// </summary>
        /// <param name="resolvedSites">List of sites to expand</param>
        /// <returns>List of list with batches of sites to expand</returns>
        private List<List<string>> CreateExpandBatches(List<string> resolvedSites)
        {
            // How many batches do we need, can't have more batches then sites to expand
            int numberOfBatches = Math.Min(resolvedSites.Count, this.maximumThreads);
            // Size of batch
            int batchCount = (resolvedSites.Count / numberOfBatches);
            // Increase batch size by 1 to avoid the last batch being overloaded, rahter spread out over all batches and make the last batch smaller
            if (resolvedSites.Count % numberOfBatches != 0)
            {
                batchCount++;
            }

            // Initialize batching variables
            List<List<string>> batches = new List<List<string>>(numberOfBatches);
            List<string> sitesBatch = new List<string>(batchCount);
            int batchCounter = 0;
            int batchesAdded = 1;

            for (int i = 0; i < resolvedSites.Count; i++)
            {
                sitesBatch.Add(resolvedSites[i]);
                batchCounter++;

                // we've filled one batch, let's create a new one
                if (batchCounter == batchCount && batchesAdded < numberOfBatches)
                {
                    batches.Add(sitesBatch);
                    batchesAdded++;
                    sitesBatch = new List<string>(batchCount);
                    batchCounter = 0;
                }
            }

            // add the last batch to the list of batches
            if (sitesBatch.Count > 0)
            {
                batches.Add(sitesBatch);
            }

            return batches;
        }

        /// <summary>
        /// Expands and individual site into sub sites
        /// </summary>
        /// <param name="resolvedSitesAndSubSites">list of sites and subsites resulting from the expanding</param>
        /// <param name="site">site to expand</param>
        private void ExpandSite(List<string> resolvedSitesAndSubSites, string site)
        {
            try
            {
                ClientContext ccExpand = CreateClientContext(site);
                IEnumerable<string> expandedSites = GetAllSubSites(ccExpand.Site);
                resolvedSitesAndSubSites.AddRange(expandedSites);
            }
            catch (WebException ex)
            {
                if (IsInternalServerErrorException(ex) || IsNotFoundException(ex))
                {
                    //eath the exception
                }
                else
                {
                    throw;
                }
            }
        }

        /// <summary>
        /// Creates a ClientContext object based on the set AuthenticationType and the used version of SharePoint
        /// </summary>
        /// <param name="site">Site Url to create a ClientContext for</param>
        /// <returns>The created ClientContext object. Returns null if no ClientContext was created</returns>
        internal ClientContext CreateClientContext(string site)
        {
            if (SharePointVersion == 15)
            {
                if (AuthenticationType == AuthenticationType.NetworkCredentials)
                {
                    return GetAuthenticationManager(site).GetNetworkCredentialAuthenticatedContext(site, username, password, domain);
                }
                else if (AuthenticationType == AuthenticationType.AppOnly)
                {
                    return GetAuthenticationManager(site).GetAppOnlyAuthenticatedContext(site, this.realm, this.clientId, this.clientSecret);
                }
            }
            else
            {
#if !ONPREMISES
                if (AuthenticationType == AuthenticationType.Office365)
                {
                    return GetAuthenticationManager(site).GetSharePointOnlineAuthenticatedContextTenant(site, username, password);
                }
                else if (AuthenticationType == AuthenticationType.AppOnly)
                {
                    return GetAuthenticationManager(site).GetAppOnlyAuthenticatedContext(site, this.realm, this.clientId, this.clientSecret);
                }
#else
                if (AuthenticationType == AuthenticationType.NetworkCredentials)
                {
                    return GetAuthenticationManager(site).GetNetworkCredentialAuthenticatedContext(site, username, password, domain);
                }
                else if (AuthenticationType == AuthenticationType.AppOnly)
                {
                    return GetAuthenticationManager(site).GetAppOnlyAuthenticatedContext(site, this.realm, this.clientId, this.clientSecret);
                }
#endif
            }

            return null;
        }

        /// <summary>
        /// Resolves a wildcard site Url into a list of actual site Url's
        /// </summary>
        /// <param name="site">Wildcard site Url to resolve</param>
        /// <param name="resolvedSites">List of resolved site Url's</param>
        private void ResolveSite(string site, List<string> resolvedSites)
        {
            if (SharePointVersion == 15)
            {
                //Good we can use search...searching requires a valid client context, so we assume that the top level site exists and is accessible for the passed creds
                ClientContext ccEnumerate = GetAuthenticationManager(site).GetNetworkCredentialAuthenticatedContext(GetTopLevelSite(site.Replace("*", "")), EnumerationUser, EnumerationPassword, EnumerationDomain);
                SiteEnumeration.Instance.ResolveSite(ccEnumerate, site, resolvedSites);
            }
            else
            {
                ClientContext ccEnumerate = null;
                //Good, we can use search for user profile and tenant API enumeration for regular sites
#if !ONPREMISES
                if (AuthenticationType == AuthenticationType.AppOnly)
                {
                    // with the proper tenant scoped permissions one can do search with app-only in SPO
                    ccEnumerate = GetAuthenticationManager(site).GetAppOnlyAuthenticatedContext(GetTenantAdminSite(site), this.realm, this.clientId, this.clientSecret);
                }
                else
                {
                    ccEnumerate = GetAuthenticationManager(site).GetSharePointOnlineAuthenticatedContextTenant(GetTenantAdminSite(site), EnumerationUser, EnumerationPassword);
                }
                Tenant tenant = new Tenant(ccEnumerate);
                SiteEnumeration.Instance.ResolveSite(tenant, site, resolvedSites, this.excludeOD4B);
#else
                ccEnumerate = GetAuthenticationManager(site).GetNetworkCredentialAuthenticatedContext(GetTopLevelSite(site.Replace("*", "")), EnumerationUser, EnumerationPassword, EnumerationDomain);
                SiteEnumeration.Instance.ResolveSite(ccEnumerate, site, resolvedSites);
#endif
            }
        }

        /// <summary>
        /// Gets all sub sites for a given site
        /// </summary>
        /// <param name="site">Site to find all sub site for</param>
        /// <returns>IEnumerable of strings holding the sub site urls</returns>
        public IEnumerable<string> GetAllSubSites(Site site)
        {
            var siteContext = site.Context;
            siteContext.Load(site, s => s.Url);
            siteContext.ExecuteQueryRetry();
            
            var queue = new Queue<string>();
            queue.Enqueue(site.Url);
            while (queue.Count > 0)
            {
                var currentUrl = queue.Dequeue();

                // No need to scan for subsites in add-in webs
                if (new Uri(site.Url).Host == new Uri(currentUrl).Host)
                {
                    using (var webContext = siteContext.Clone(currentUrl))
                    {
                        webContext.Load(webContext.Web, web => web.Webs.Include(w => w.Url, w => w.WebTemplate));
                        webContext.ExecuteQueryRetry();
                        foreach (var subWeb in webContext.Web.Webs)
                        {
                            if (!subWeb.WebTemplate.Equals("App", StringComparison.InvariantCultureIgnoreCase))
                            {
                                queue.Enqueue(subWeb.Url);
                            }
                        }
                    }
                }

                yield return currentUrl;
            }
        }
#endregion

        #region Helper methods
        /// <summary>
        /// Verifies if the passed Url has a valid structure
        /// </summary>
        /// <param name="url">Url to validate</param>
        /// <returns>True is valid, false otherwise</returns>
        private bool IsValidUrl(string url)
        {
            Uri uri;

            if (Uri.TryCreate(url, UriKind.Absolute, out uri))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Gets the current SharePoint version based on the loaded assembly
        /// </summary>
        /// <returns></returns>
        private int GetSharePointVersion()
        {
            Assembly asm = Assembly.GetAssembly(typeof(Site));
            return asm.GetName().Version.Major;
        }

        /// <summary>
        /// Gets the tenant admin site based on the tenant name provided when setting the authentication details
        /// </summary>
        /// <returns>The tenant admin site</returns>
        private string GetTenantAdminSite(string site)
        {
            if (!String.IsNullOrEmpty(this.tenantAdminSite))
            {
                return this.tenantAdminSite;
            }
            else
            {
                Uri u = new Uri(GetTopLevelSite(site.Replace("*", "")));
                string tenantName = u.DnsSafeHost.Split(new string[] { "." }, StringSplitOptions.RemoveEmptyEntries)[0];
                return String.Format("https://{0}-admin.sharepoint.com", tenantName);
            }
        }

        /// <summary>
        /// Gets the top level site for the given url
        /// </summary>
        /// <param name="site"></param>
        /// <returns></returns>
        private string GetTopLevelSite(string site)
        {
            Uri uri = new Uri(site.TrimEnd(new[] { '/' }));
            return string.Format("{0}://{1}", uri.Scheme, uri.DnsSafeHost);
        }

        /// <summary>
        /// Gets the root site for a given site Url
        /// </summary>
        /// <param name="site">Site Url</param>
        /// <returns>Root site Url of the given site Url</returns>
        private string GetRootSite(string site)
        {
            Uri uri = new Uri(site.TrimEnd(new[] { '/' }));

            //e.g. https://bertonline.sharepoint.com
            if (String.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath.Equals("/", StringComparison.InvariantCultureIgnoreCase))
            {
                // Site must be root site, no doubts possible
                return string.Format("{0}://{1}", uri.Scheme, uri.DnsSafeHost);
            }

            string[] siteParts = uri.AbsolutePath.Split(new string[] { "/" }, StringSplitOptions.RemoveEmptyEntries);

            // e.g. https://bertonline.sharepoint.com/sub1
            // e.g. https://bertonline.sharepoint.com/sub1/sub11/sub111
            // e.g. https://bertonline.sharepoint.com/sites/dev/sub1
            if (siteParts.Length == 1 || siteParts.Length > 2)
            {
                if (siteParts.Length == 1)
                {
                    // e.g. https://bertonline.sharepoint.com/search is a special case
                    if (siteParts[0].Equals("search", StringComparison.InvariantCultureIgnoreCase))
                    {
                        return string.Format("{0}://{1}/{2}", uri.Scheme, uri.DnsSafeHost, siteParts[0]);
                    }
                    else
                    {
                        return string.Format("{0}://{1}", uri.Scheme, uri.DnsSafeHost);
                    }
                }
                else
                {
                    if (siteParts[0].Equals("sites", StringComparison.InvariantCultureIgnoreCase) ||
                        siteParts[0].Equals("teams", StringComparison.InvariantCultureIgnoreCase) ||
                        siteParts[0].Equals("personal", StringComparison.InvariantCultureIgnoreCase) ||
                        siteParts[0].Equals("portals", StringComparison.InvariantCultureIgnoreCase))
                    {
                        return string.Format("{0}://{1}/{2}/{3}", uri.Scheme, uri.DnsSafeHost, siteParts[0], siteParts[1]);
                    }
                    else
                    {
                        return string.Format("{0}://{1}", uri.Scheme, uri.DnsSafeHost);
                    }
                }
            }
            else
            {
                // e.g. https://bertonline.sharepoint.com/sub1/sub11
                // e.g. https://bertonline.sharepoint.com/sites/dev
                if (siteParts[0].Equals("sites", StringComparison.InvariantCultureIgnoreCase) ||
                    siteParts[0].Equals("teams", StringComparison.InvariantCultureIgnoreCase) ||
                    siteParts[0].Equals("personal", StringComparison.InvariantCultureIgnoreCase) ||
                    siteParts[0].Equals("portals", StringComparison.InvariantCultureIgnoreCase))
                {
                    // sites and teams are default managed paths, so assume this is a root site
                    return site;
                }
                else
                {
                    return string.Format("{0}://{1}", uri.Scheme, uri.DnsSafeHost);
                }
            }
        }

        /// <summary>
        /// Normalizes the timer job name
        /// </summary>
        /// <param name="timerJobName">Timer job name</param>
        /// <returns>Normalized timer job name</returns>
        private string NormalizedTimerJobName(string timerJobName)
        {
            return timerJobName.Replace(" ", "_");
        }

        /// <summary>
        /// Returns true if the exception was a "The remote server returned an error: (500) Internal Server Error"
        /// </summary>
        /// <param name="ex">Exception to examine</param>
        /// <returns>True if "The remote server returned an error: (500) Internal Server Error" exception, false otherwise</returns>
        private bool IsInternalServerErrorException(Exception ex)
        {
            if (ex is WebException)
            {
                if (ex.HResult == -2146233079 && ex.Message.IndexOf("(500)") > -1)
                {
                    return true;
                }
                else
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// Returns true if the exception was a "The remote server returned an error: (404) Not Found"
        /// </summary>
        /// <param name="ex">Exception to examine</param>
        /// <returns>True if "The remote server returned an error: (404) Not Found" exception, false otherwise</returns>
        private bool IsNotFoundException(Exception ex)
        {
            if (ex is WebException)
            {
                if (ex.HResult == -2146233079 && ex.Message.IndexOf("(404)") > -1)
                {
                    return true;
                }
                else
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }
        #endregion
    }
}

﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using GrpcServer;

namespace TypeDBCustom
{ 

    public enum QueryType
    {
        Define,
        Undefine,
        Match,
        Update,
        Delete,
        Insert
    }

    public class CoreClient
    {

        #region Constructor

        /// <summary>
        /// Initialize the object and connects to remote TypeDB server using gRPC
        /// </summary>
        /// <param name="host">hostname or IP for TypeDB server</param>
        /// <param name="port">port for TypeDB server</param>
        public CoreClient(string host, int port)
        {
            grpcChannel = new Grpc.Core.Channel(host, port, Grpc.Core.ChannelCredentials.Insecure);
            Client = new TypeDB.TypeDBClient(grpcChannel);

            // initialize the timer object for pulsing the session
            tPulse = new Timer((obj) =>
            {
                pulseResp = Client.session_pulse(pulseRequest);
                Debug.WriteLine(pulseResp.Alive);
            }, null, Timeout.Infinite, Timeout.Infinite);
        }

        #endregion 

        #region Variables & Properties

        #region Public

        /// <summary>
        /// this is the TypeDBClient which is generated by protocol
        /// </summary>
        public TypeDB.TypeDBClient Client = null;

        /// <summary>
        /// this will be used to pass the transaction requesst to bi-directional stream
        /// </summary>
        public readonly Transaction.Types.Client transactionClient = new Transaction.Types.Client();

        /// <summary>
        /// This will be used to hold the current database name
        /// </summary>
        public string CurrentDatabase { get; private set; }

        /// <summary>
        /// This is copy of original Session ID received from server
        /// this is assigned when the connection to server made
        /// it will be used in every transaction open transaction
        /// </summary>
        public Google.Protobuf.ByteString SessionID { get; private set; }
    
        #endregion

        #region Private

        /// <summary>
        /// This will generate random request id everytime this property requested
        /// </summary>
        private Google.Protobuf.ByteString RandonReqId {
            get
            {
                return Google.Protobuf.ByteString.CopyFromUtf8(new Guid(SessionID.ToByteArray()).ToString());
            }
        }

        // this will hold the response for session pulse 
        private Session.Types.Pulse.Types.Res pulseResp = null;
        // this will be pulse request object that will be passed to client for session pulse
        static Session.Types.Pulse.Types.Req pulseRequest = null;
        // this timer will be used to automatically send pulse message every 5 seconds 
        private readonly Timer tPulse = null;
        // this will reference the host and port to typeDB protocol client
        private Grpc.Core.Channel grpcChannel = null;

        #endregion

        #endregion

        #region Databases

        /// <summary>
        /// This will get all the available databases on TypeDB server
        /// </summary>
        /// <returns>All the available database names on TypeDB server</returns>
        public string[] GetAllDatabases()
        {
            var databases = Client.databases_all(new CoreDatabaseManager.Types.All.Types.Req() { });
            return databases.Names.ToArray<string>();
        }

        /// <summary>
        /// This will create the database for you in TypeDB server
        /// </summary>
        /// <param name="DatabaseName"></param>
        public void CreateDatabase(string DatabaseName)
        {
            //create the databse in the server
            Client.databases_create(new CoreDatabaseManager.Types.Create.Types.Req() { Name = DatabaseName });
        }

        /// <summary>
        /// this method will open the session with server for specific database
        /// if the connection successfull it will start pulse automatically.
        /// </summary>
        /// <param name="DatabaseName">The name of the database you want to link</param>
        public void OpenDatabase(string DatabaseName, Session.Types.Type sessionType = Session.Types.Type.Data)
        {
            // set the current database name
            CurrentDatabase = DatabaseName;
            //creates new session request and pass the database name session type as parameters. it will open new session for TypeDB server
            Session.Types.Open.Types.Res session = Client.session_open(new Session.Types.Open.Types.Req()
            {
                Database = DatabaseName,
                Type = sessionType
            }, null, null, CancellationToken.None);
            SessionID = session.SessionId;
            // fills the session id to pulse request so it can be used in session_pulse command
            pulseRequest = new Session.Types.Pulse.Types.Req() { SessionId = session.SessionId };
            // starts the timer with 5 seconds interval
            tPulse.Change(0, 5000);

        }

        /// <summary>
        /// This Method will return you the full schema information 
        /// This schema information is for current database
        /// </summary>
        /// <returns></returns>
        public string GetSchema()
        {
            return Client.database_schema(new CoreDatabase.Types.Schema.Types.Req() { Name = CurrentDatabase }).Schema;
        }

        /// <summary>
        /// This function will check if provided database exist in the server
        /// This search is based on database name
        /// </summary>
        /// <param name="dbName">Name of database to check</param>
        /// <returns>True if exits, otherwise false</returns>
        public bool IsDatabaseExist(string dbName)
        {
            // create the request to check for database 
            var results = Client.databases_contains(new CoreDatabaseManager.Types.Contains.Types.Req() { Name = dbName });
            return results.Contains;
        }

        /// <summary>
        /// this will disable the session pulse interval and closes the session for database
        /// </summary>
        public void CloseDatabase()
        {

            // changing this to infinite will stop the timer and clear the interval
            tPulse.Change(Timeout.Infinite, Timeout.Infinite);
            // create the session close request and pass it to client object with session ID
            Client.session_close(new Session.Types.Close.Types.Req()
            {
                SessionId = pulseRequest.SessionId
            });
            
        }

        #endregion

        #region Transactions

        /// <summary>
        /// If the bi-directional stream still have pending data it will message in CONTINUE, 
        /// in that case, this will be passed the transaction client and write to stream so we can have rest of data
        /// if the stream response DONE instead of CONTINUE then we don't need to use this.
        /// </summary>
        public static Transaction.Types.Req trans_stream = new Transaction.Types.Req()
        {
            StreamReq = new Transaction.Types.Stream.Types.Req() { }
        };

        /// <summary>
        /// this will be used for transaction open request for bi-directional stream
        /// this will create the bi-directional stream for specified req id and session id
        /// this will be used in every transaction.
        /// </summary>
        private static readonly Transaction.Types.Req trans_open = new Transaction.Types.Req()
        {
            OpenReq = new Transaction.Types.Open.Types.Req()
            {
                SessionId = Google.Protobuf.ByteString.CopyFromUtf8("req_open"),
                Type = Transaction.Types.Type.Read
            }
        };

        /// <summary>
        /// this will be used for transaction commit request for bi-directional stream
        /// this will create the bi-directional stream for specified req id and session id
        /// this must be used after every data write request
        /// </summary>
        private static readonly Transaction.Types.Req trans_commit = new Transaction.Types.Req()
        {
            CommitReq = new Transaction.Types.Commit.Types.Req() { }
        };
        
        /// <summary>
        /// This method will create a open transaction request
        /// Open Transaction request is must before any other request
        /// </summary>
        /// <param name="ReqID">This unique ID will be used through out the stream</param>
        /// <param name="Transactions">The stream object that will be used to write transaction</param>
        private void OpenTransaction(ref Google.Protobuf.ByteString SessionID,
            Transaction.Types.Type TransType,
            ref Google.Protobuf.ByteString ReqID,
            ref AsyncDuplexStreamingCall<Transaction.Types.Client, Transaction.Types.Server> Transactions)
        {

            // clear the existing transactions if there are any.
            transactionClient.Reqs.Clear();
            
            // fill the sessionId in transaction open request. it will open the stream for communication.
            trans_open.OpenReq.SessionId = SessionID;
            trans_open.ReqId = ReqID;
            trans_open.OpenReq.Type = TransType;
            // add the transaction open request to transaction client.
            transactionClient.Reqs.Add(trans_open);
            // use the transaction client to write the transaction on stream
            Transactions.RequestStream.WriteAsync(transactionClient).GetAwaiter().GetResult();
            // move the read to next element so server response will be clear from open transaction resp
            Transactions.ResponseStream.MoveNext(CancellationToken.None).GetAwaiter().GetResult();

        }

        /// <summary>
        /// This method will be used to make the close request
        /// this method must be used after any read operation
        /// otherwise, the already open exception maybe thrown
        /// </summary>
        /// <param name="Transactions">The stream object that will be used to read/write transaction</param>
        private void CloseTransaction(ref AsyncDuplexStreamingCall<Transaction.Types.Client, Transaction.Types.Server> Transactions)
        {
            // closes the transaction stream
            Transactions.RequestStream.CompleteAsync().GetAwaiter().GetResult();
            Transactions.Dispose();
        }

        /// <summary>
        /// This method used to commit changes made to data or schema
        /// This method must be called after every write request to server
        /// otherwise, changes to data maybe lost
        /// </summary>
        /// <param name="ReqID">This unique ID will be used through out the stream</param>
        /// <param name="Transactions">The stream object that will be used to write transaction</param>
        private void CommitChanges(
            ref Google.Protobuf.ByteString ReqID, 
            ref AsyncDuplexStreamingCall<Transaction.Types.Client, Transaction.Types.Server> Transactions)
        {

            // clear the existing transactions if there are any.
            transactionClient.Reqs.Clear();

            // fill the sessionId in transaction open request. it will open the stream for communication.
            trans_commit.ReqId = ReqID;
            // add the transaction open request to transaction client.
            transactionClient.Reqs.Add(trans_commit);
            // use the transaction client to write the transaction on stream
            Transactions.RequestStream.WriteAsync(transactionClient).GetAwaiter().GetResult();
            // move the read to next element so server response will be clear from open transaction resp
            Transactions.ResponseStream.MoveNext(CancellationToken.None).GetAwaiter().GetResult();

        }

        /// <summary>
        /// this is check if the stream have done sending the data and we exit the loop,
        /// otherwise we need to send the continue stream request to get the rest of data
        /// if this miss you will stuck on MoveNext 
        /// </summary>
        /// <param name="ServerResp">The current Response from server</param>
        /// <param name="ReqID">This unique ID will be used through out the stream</param>
        /// <param name="Transactions">The stream object that will be used to read/write transaction</param>
        /// <returns></returns>
        private bool CheckIfStreamEnd(ref Transaction.Types.Server ServerResp,
            ref Google.Protobuf.ByteString ReqID,
            ref AsyncDuplexStreamingCall<Transaction.Types.Client, Transaction.Types.Server> Transactions)
        {

            // check if the response or response part have data in it
            if (ServerResp.Res != null)
                return false;
            
            if (ServerResp.ResPart.ResCase == Transaction.Types.ResPart.ResOneofCase.StreamResPart &&
                    ServerResp.ResPart.StreamResPart.State == Transaction.Types.Stream.Types.State.Done)
            {
                return true;
            }
            else if (ServerResp.ResPart.ResCase == Transaction.Types.ResPart.ResOneofCase.StreamResPart &&
                ServerResp.ResPart.StreamResPart.State == Transaction.Types.Stream.Types.State.Continue)
            {
                // sending the transaction stream request to server for receiving pending data
                transactionClient.Reqs.Clear();
                trans_stream.ReqId = ReqID;
                transactionClient.Reqs.Add(trans_stream);
                Transactions.RequestStream.WriteAsync(transactionClient).GetAwaiter().GetResult();
                return false;
            }
            else
            {
                return false;
            }

        }

        /// <summary>
        /// This method will execute the query
        /// and return the results in ConceptMap Array
        /// </summary>
        /// <param name="queryText">Query you want to execute</param>
        /// <returns></returns>
        public IEnumerable<ConceptMap> ExecuteQuery(string queryText, QueryType queryType)
        {

            // this will be used to hold the session if needed
            Session.Types.Open.Types.Res session = null;
            Google.Protobuf.ByteString sessionID = SessionID;
            // this will be unique transaction id for this query
            var ReqID = RandonReqId;
            // creates the bi-directional stream for transactions
            var Transactions = Client.transaction(null, null, CancellationToken.None);
            switch (queryType)
            {
                case QueryType.Update:
                case QueryType.Delete:
                case QueryType.Insert:
                    // call the method to open transaction
                    OpenTransaction(ref sessionID, Transaction.Types.Type.Write, ref ReqID, ref Transactions);
                    break;

                case QueryType.Define:
                case QueryType.Undefine:
                    //creates new session request and pass the database name session type as parameters. it will open new session for TypeDB server
                    session = Client.session_open(new Session.Types.Open.Types.Req()
                    {
                        Database = CurrentDatabase,
                        Type = Session.Types.Type.Schema
                    }, null, null, CancellationToken.None);
                    sessionID = session.SessionId;

                    // call the method to open transaction
                    OpenTransaction(ref sessionID, Transaction.Types.Type.Write, ref ReqID, ref Transactions);
                    break;

                case QueryType.Match:
                default:
                    // call the method to open transaction
                    OpenTransaction(ref sessionID, Transaction.Types.Type.Read, ref ReqID, ref Transactions);
                    break;
            }            

            // this is how we setup a match query, for different query type you have to use different property of query object
            QueryManager.Types.Req query = new QueryManager.Types.Req()
            {                
                Options = new Options() { Parallel = true }
            };
            switch (queryType)
            {
                case QueryType.Match:
                    query.MatchReq = new QueryManager.Types.Match.Types.Req() { Query = queryText };
                    break;
                case QueryType.Define:
                    query.DefineReq = new QueryManager.Types.Define.Types.Req() { Query = queryText };
                    break;
                case QueryType.Undefine:
                    query.UndefineReq = new QueryManager.Types.Undefine.Types.Req() { Query = queryText };
                    break;
                case QueryType.Update:
                    query.UpdateReq = new QueryManager.Types.Update.Types.Req() { Query = queryText };
                    break;
                case QueryType.Delete:
                    query.DeleteReq = new QueryManager.Types.Delete.Types.Req() { Query = queryText };
                    break;
                case QueryType.Insert:
                    query.InsertReq = new QueryManager.Types.Insert.Types.Req() { Query = queryText };
                    break;
                default:
                    yield break;
            }

            // clear the existing transactions if there are any.
            transactionClient.Reqs.Clear();
            //you can add multiple transaction queries at once
            transactionClient.Reqs.Add(new Transaction.Types.Req() { 
                QueryManagerReq = query, 
                ReqId = ReqID
            });
            //write the transaction to bi-directional stream
            Transactions.RequestStream.WriteAsync(transactionClient).GetAwaiter().GetResult();
            var Headers = Transactions.ResponseHeadersAsync.GetAwaiter().GetResult();

            Transaction.Types.Server ServerResp = null;
            // this is like an enumrator, you have to call MoveNext for every chunk of data you will receive
            while (Transactions.ResponseStream.MoveNext(CancellationToken.None).GetAwaiter().GetResult())
            {
                ServerResp = Transactions.ResponseStream.Current; // set the current enumrator object to local so can access it shortly

                // check if the server response is part 
                if (ServerResp.Res != null)
                {

                    // check if query manager is null
                    if (ServerResp.Res.QueryManagerRes == null)
                        continue;

                    // check the response type, loop through every conceptmap in response, yield it
                    switch (ServerResp.Res.QueryManagerRes.ResCase)
                    {
                        case QueryManager.Types.Res.ResOneofCase.DefineRes:
                        case QueryManager.Types.Res.ResOneofCase.UndefineRes:
                        case QueryManager.Types.Res.ResOneofCase.DeleteRes:
                            break;
                    }
                    break;

                }
                else
                {

                    // check if stream end
                    if (CheckIfStreamEnd(ref ServerResp, ref ReqID, ref Transactions))
                        break;

                    // check if the query manager is null
                    if (ServerResp.ResPart.QueryManagerResPart == null)
                        continue;

                    // check the response type, loop through every conceptmap in response, yield it
                    switch (ServerResp.ResPart.QueryManagerResPart.ResCase)
                    {
                        case QueryManager.Types.ResPart.ResOneofCase.MatchResPart:
                            foreach (var concept in ServerResp.ResPart.QueryManagerResPart.MatchResPart.Answers)
                                yield return concept;
                            break;
                        case QueryManager.Types.ResPart.ResOneofCase.InsertResPart:
                            foreach (var concept in ServerResp.ResPart.QueryManagerResPart.InsertResPart.Answers)
                                yield return concept;
                            break;
                        case QueryManager.Types.ResPart.ResOneofCase.UpdateResPart:
                            foreach (var concept in ServerResp.ResPart.QueryManagerResPart.UpdateResPart.Answers)
                                yield return concept;
                            break;
                        default:
                            break;
                    }

                }

            }

            // check if the changes need to commit
            switch (queryType)
            {
                case QueryType.Update:
                case QueryType.Delete:
                case QueryType.Insert:
                case QueryType.Define:
                case QueryType.Undefine:
                    // commit the changes to server
                    CommitChanges(ref ReqID, ref Transactions);
                    break;

                case QueryType.Match:
                default:
                    break;
            }
            // closes the stream
            CloseTransaction(ref Transactions);

            // check if new session created, then close it
            if (session != null)
                Client.session_close(new Session.Types.Close.Types.Req() { SessionId = sessionID });

        }

        /// <summary>
        /// This function will return all the attributes available in database
        /// </summary>
        public IEnumerable<ConceptMap> GetAllAttributes()
        {

            var results = ExecuteQuery("match $x sub attribute; get $x;", QueryType.Match);
            foreach (var result in results)
                yield return result;

        }

        /// <summary>
        /// This function will return all the relations available in database
        /// </summary>
        public IEnumerable<ConceptMap> GetAllRelations()
        {
             
            var results = ExecuteQuery("match $x sub relation; get $x;", QueryType.Match);
            foreach (var result in results)
                yield return result;

        }

        /// <summary>
        /// This function will return all the entities available in database
        /// </summary>
        public IEnumerable<ConceptMap> GetAllEntities()
        {
             
            var results = ExecuteQuery("match $x sub entity; get $x;", QueryType.Match);
            foreach (var result in results)
                yield return result;

        }

        /// <summary>
        /// This function will return all the attributes of specific entity
        /// this function also work with relation names
        /// </summary>
        /// <param name="typeName">Name of the entity or Relation</param>
        public Dictionary<string, GrpcServer.Type> GetAttributes(string typeName)
        {

            // initialize the new dictionary to hold the concepts
            Dictionary<string, GrpcServer.Type> results = new Dictionary<string, GrpcServer.Type>();

            // get the attributes of the specific entity
            var QueryResults = ExecuteQuery($"match $p isa {typeName}; $p has attribute $a; get $a;", QueryType.Match);
            foreach (var result in QueryResults)
            {
                // get the mapping value for concept
                result.Map.TryGetValue("a", out Concept concept);

                switch (concept.ConceptCase)
                {
                    case Concept.ConceptOneofCase.Thing:
                        // check if dictionary already have the attribute
                        if (results.ContainsKey(concept.Thing.Type.Label))
                            break;
                        // add the label to dictionary
                        results.Add(concept.Thing.Type.Label, concept.Thing.Type);
                        break;

                    case Concept.ConceptOneofCase.Type:
                        // check if dictionary already have the attribute
                        if (results.ContainsKey(concept.Type.Label))
                            break;
                        // add the label to dictionary
                        results.Add(concept.Type.Label, concept.Type);
                        break;
                }

            }

            return results;

        }

        #endregion

    }
}

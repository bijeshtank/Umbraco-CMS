﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Web.Http;
using System.Web.Http.Controllers;
using System.Web.Http.ModelBinding;
using AutoMapper;
using Umbraco.Core;
using Umbraco.Core.Logging;
using Umbraco.Core.Models;
using Umbraco.Core.Models.Membership;
using Umbraco.Core.Persistence.DatabaseModelDefinitions;
using Umbraco.Core.Services;
using Umbraco.Web.Models.ContentEditing;
using Umbraco.Web.Models.Mapping;
using Umbraco.Web.Mvc;
using Umbraco.Web.WebApi;
using Umbraco.Web.WebApi.Filters;
using Umbraco.Core.Persistence.Querying;
using Umbraco.Web.PublishedCache;
using Umbraco.Core.Events;
using Umbraco.Core.Models.Validation;
using Umbraco.Web.Composing;
using Umbraco.Web.Models;
using Umbraco.Web.WebServices;
using Umbraco.Web._Legacy.Actions;
using Constants = Umbraco.Core.Constants;
using Language = Umbraco.Web.Models.ContentEditing.Language;
using Umbraco.Core.PropertyEditors;
using Umbraco.Web.Editors.Binders;
using Umbraco.Web.Editors.Filters;


namespace Umbraco.Web.Editors
{
    /// <summary>
    /// The API controller used for editing content
    /// </summary>
    /// <remarks>
    /// This controller is decorated with the UmbracoApplicationAuthorizeAttribute which means that any user requesting
    /// access to ALL of the methods on this controller will need access to the content application.
    /// </remarks>
    [PluginController("UmbracoApi")]
    [UmbracoApplicationAuthorize(Constants.Applications.Content)]
    [ContentControllerConfiguration]
    public class ContentController : ContentControllerBase
    {
        private readonly IPublishedSnapshotService _publishedSnapshotService;
        private readonly PropertyEditorCollection _propertyEditors;

        public ContentController(IPublishedSnapshotService publishedSnapshotService, PropertyEditorCollection propertyEditors)
        {
            if (publishedSnapshotService == null) throw new ArgumentNullException(nameof(publishedSnapshotService));
            _publishedSnapshotService = publishedSnapshotService;
            _propertyEditors = propertyEditors ?? throw new ArgumentNullException(nameof(propertyEditors));
        }

        /// <summary>
        /// Configures this controller with a custom action selector
        /// </summary>
        private class ContentControllerConfigurationAttribute : Attribute, IControllerConfiguration
        {
            public void Initialize(HttpControllerSettings controllerSettings, HttpControllerDescriptor controllerDescriptor)
            {
                controllerSettings.Services.Replace(typeof(IHttpActionSelector), new ParameterSwapControllerActionSelector(
                    new ParameterSwapControllerActionSelector.ParameterSwapInfo("GetNiceUrl", "id", typeof(int), typeof(Guid), typeof(Udi)),
                    new ParameterSwapControllerActionSelector.ParameterSwapInfo("GetById", "id", typeof(int), typeof(Guid), typeof(Udi))    
                ));
            }
        }

        /// <summary>
        /// Returns true if any content types have culture variation enabled
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [WebApi.UmbracoAuthorize, OverrideAuthorization]
        public bool AllowsCultureVariation()
        {
            var contentTypes = Services.ContentTypeService.GetAll();
            return contentTypes.Any(contentType => contentType.VariesByCulture());
        }

        /// <summary>
        /// Return content for the specified ids
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        [FilterAllowedOutgoingContent(typeof(IEnumerable<ContentItemDisplay>))]
        public IEnumerable<ContentItemDisplay> GetByIds([FromUri]int[] ids)
        {
            //fixme what about cultures?

            var foundContent = Services.ContentService.GetByIds(ids);
            return foundContent.Select(x => MapToDisplay(x));
        }

        /// <summary>
        /// Updates the permissions for a content item for a particular user group
        /// </summary>
        /// <param name="saveModel"></param>
        /// <returns></returns>
        /// <remarks>
        /// Permission check is done for letter 'R' which is for <see cref="ActionRights"/> which the user must have access to to update
        /// </remarks>
        [EnsureUserPermissionForContent("saveModel.ContentId", 'R')]
        public IEnumerable<AssignedUserGroupPermissions> PostSaveUserGroupPermissions(UserGroupPermissionsSave saveModel)
        {
            if (saveModel.ContentId <= 0) throw new HttpResponseException(Request.CreateResponse(HttpStatusCode.NotFound));

            //TODO: Should non-admins be alowed to set granular permissions?

            var content = Services.ContentService.GetById(saveModel.ContentId);
            if (content == null) throw new HttpResponseException(Request.CreateResponse(HttpStatusCode.NotFound));

            //current permissions explicitly assigned to this content item
            var contentPermissions = Services.ContentService.GetPermissions(content)
                .ToDictionary(x => x.UserGroupId, x => x);

            var allUserGroups = Services.UserService.GetAllUserGroups().ToArray();

            //loop through each user group
            foreach (var userGroup in allUserGroups)
            {
                //check if there's a permission set posted up for this user group
                IEnumerable<string> groupPermissions;
                if (saveModel.AssignedPermissions.TryGetValue(userGroup.Id, out groupPermissions))
                {
                    //create a string collection of the assigned letters
                    var groupPermissionCodes = groupPermissions.ToArray();

                    //check if there are no permissions assigned for this group save model, if that is the case we want to reset the permissions
                    //for this group/node which will go back to the defaults
                    if (groupPermissionCodes.Length == 0)
                    {
                        Services.UserService.RemoveUserGroupPermissions(userGroup.Id, content.Id);
                    }
                    //check if they are the defaults, if so we should just remove them if they exist since it's more overhead having them stored
                    else if (userGroup.Permissions.UnsortedSequenceEqual(groupPermissionCodes))
                    {
                        //only remove them if they are actually currently assigned
                        if (contentPermissions.ContainsKey(userGroup.Id))
                        {
                            //remove these permissions from this node for this group since the ones being assigned are the same as the defaults
                            Services.UserService.RemoveUserGroupPermissions(userGroup.Id, content.Id);
                        }
                    }
                    //if they are different we need to update, otherwise there's nothing to update
                    else if (contentPermissions.ContainsKey(userGroup.Id) == false || contentPermissions[userGroup.Id].AssignedPermissions.UnsortedSequenceEqual(groupPermissionCodes) == false)
                    {

                        Services.UserService.ReplaceUserGroupPermissions(userGroup.Id, groupPermissionCodes.Select(x => x[0]), content.Id);
                    }
                }
            }

            return GetDetailedPermissions(content, allUserGroups);
        }

        /// <summary>
        /// Returns the user group permissions for user groups assigned to this node
        /// </summary>
        /// <param name="contentId"></param>
        /// <returns></returns>
        /// <remarks>
        /// Permission check is done for letter 'R' which is for <see cref="ActionRights"/> which the user must have access to to view
        /// </remarks>
        [EnsureUserPermissionForContent("contentId", 'R')]
        public IEnumerable<AssignedUserGroupPermissions> GetDetailedPermissions(int contentId)
        {
            if (contentId <= 0) throw new HttpResponseException(Request.CreateResponse(HttpStatusCode.NotFound));
            var content = Services.ContentService.GetById(contentId);
            if (content == null) throw new HttpResponseException(Request.CreateResponse(HttpStatusCode.NotFound));

            //TODO: Should non-admins be able to see detailed permissions?

            var allUserGroups = Services.UserService.GetAllUserGroups();

            return GetDetailedPermissions(content, allUserGroups);
        }

        private IEnumerable<AssignedUserGroupPermissions> GetDetailedPermissions(IContent content, IEnumerable<IUserGroup> allUserGroups)
        {
            //get all user groups and map their default permissions to the AssignedUserGroupPermissions model.
            //we do this because not all groups will have true assigned permissions for this node so if they don't have assigned permissions, we need to show the defaults.

            var defaultPermissionsByGroup = Mapper.Map<IEnumerable<AssignedUserGroupPermissions>>(allUserGroups).ToArray();

            var defaultPermissionsAsDictionary = defaultPermissionsByGroup
                .ToDictionary(x => Convert.ToInt32(x.Id), x => x);

            //get the actual assigned permissions
            var assignedPermissionsByGroup = Services.ContentService.GetPermissions(content).ToArray();

            //iterate over assigned and update the defaults with the real values
            foreach (var assignedGroupPermission in assignedPermissionsByGroup)
            {
                var defaultUserGroupPermissions = defaultPermissionsAsDictionary[assignedGroupPermission.UserGroupId];

                //clone the default permissions model to the assigned ones
                defaultUserGroupPermissions.AssignedPermissions = AssignedUserGroupPermissions.ClonePermissions(defaultUserGroupPermissions.DefaultPermissions);

                //since there is custom permissions assigned to this node for this group, we need to clear all of the default permissions
                //and we'll re-check it if it's one of the explicitly assigned ones
                foreach (var permission in defaultUserGroupPermissions.AssignedPermissions.SelectMany(x => x.Value))
                {
                    permission.Checked = false;
                    permission.Checked = assignedGroupPermission.AssignedPermissions.Contains(permission.PermissionCode, StringComparer.InvariantCulture);
                }

            }

            return defaultPermissionsByGroup;
        }

        /// <summary>
        /// Returns an item to be used to display the recycle bin for content
        /// </summary>
        /// <returns></returns>
        public ContentItemDisplay GetRecycleBin()
        {
            var apps = new List<ContentApp>();
            apps.AppendListViewApp(Services.DataTypeService, _propertyEditors, "recycleBin", "content");
            apps[0].Active = true;
            var display = new ContentItemDisplay
            {
                Id = Constants.System.RecycleBinContent,
                ParentId = -1,
                ContentTypeAlias = "recycleBin",
                IsContainer = true,
                Path = "-1," + Constants.System.RecycleBinContent,
                Variants = new List<ContentVariantDisplay>
                {
                    new ContentVariantDisplay
                    {
                        CreateDate = DateTime.Now,
                        Name = Services.TextService.Localize("general/recycleBin")
                    }
                },
                ContentApps = apps
            };

            return display;
        }

        //fixme what about cultures?
        public ContentItemDisplay GetBlueprintById(int id)
        {
            var foundContent = Services.ContentService.GetBlueprintById(id);
            if (foundContent == null)
            {
                HandleContentNotFound(id);
            }

            var content = MapToDisplay(foundContent);

            SetupBlueprint(content, foundContent);

            return content;
        }

        private static void SetupBlueprint(ContentItemDisplay content, IContent persistedContent)
        {
            content.AllowPreview = false;

            //set a custom path since the tree that renders this has the content type id as the parent
            content.Path = string.Format("-1,{0},{1}", persistedContent.ContentTypeId, content.Id);

            content.AllowedActions = new[] { "A" };
            content.IsBlueprint = true;

            //fixme - exclude the content apps here
            //var excludeProps = new[] { "_umb_urls", "_umb_releasedate", "_umb_expiredate", "_umb_template" };
            //var propsTab = content.Tabs.Last();
            //propsTab.Properties = propsTab.Properties
            //    .Where(p => excludeProps.Contains(p.Alias) == false);
        }

        /// <summary>
        /// Gets the content json for the content id
        /// </summary>
        /// <param name="id"></param>
        /// <param name="culture"></param>
        /// <returns></returns>
        [OutgoingEditorModelEvent]
        [EnsureUserPermissionForContent("id")]
        public ContentItemDisplay GetById(int id)
        {
            var foundContent = GetObjectFromRequest(() => Services.ContentService.GetById(id));
            if (foundContent == null)
            {
                HandleContentNotFound(id);
                return null;//irrelevant since the above throws
            }
            var content = MapToDisplay(foundContent);
            return content;
        }

        /// <summary>
        /// Gets the content json for the content id
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        [OutgoingEditorModelEvent]
        [EnsureUserPermissionForContent("id")]
        public ContentItemDisplay GetById(Guid id)
        {
            var foundContent = GetObjectFromRequest(() => Services.ContentService.GetById(id));
            if (foundContent == null)
            {
                HandleContentNotFound(id);
                return null;//irrelevant since the above throws
            }

            var content = MapToDisplay(foundContent);
            return content;
        }

        /// <summary>
        /// Gets the content json for the content id
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        [OutgoingEditorModelEvent]
        [EnsureUserPermissionForContent("id")]
        public ContentItemDisplay GetById(Udi id)
        {
            var guidUdi = id as GuidUdi;
            if (guidUdi != null)
            {
                return GetById(guidUdi.Guid);
            }

            throw new HttpResponseException(HttpStatusCode.NotFound);
        }   

        /// <summary>
        /// Gets an empty content item for the
        /// </summary>
        /// <param name="contentTypeAlias"></param>
        /// <param name="parentId"></param>        
        [OutgoingEditorModelEvent]
        public ContentItemDisplay GetEmpty(string contentTypeAlias, int parentId)
        {
            var contentType = Services.ContentTypeService.Get(contentTypeAlias);
            if (contentType == null)
            {
                throw new HttpResponseException(HttpStatusCode.NotFound);
            }

            var emptyContent = Services.ContentService.Create("", parentId, contentType.Alias, Security.GetUserId().ResultOr(0));
            var mapped = MapToDisplay(emptyContent);

            //remove the listview app if it exists
            mapped.ContentApps = mapped.ContentApps.Where(x => x.Alias != "childItems").ToList();
            
            //if (contentType.VariesByCulture())
            //{
            //    //Remove all variants except for the default since currently the default must be saved before other variants can be edited
            //    //TODO: Allow for editing all variants at once ... this will be a future task
            //    mapped.Variants = new[] { mapped.Variants.FirstOrDefault(x => x.IsCurrent) };
            //}

            return mapped;
        }

        [OutgoingEditorModelEvent]
        public ContentItemDisplay GetEmpty(int blueprintId, int parentId)
        {
            var blueprint = Services.ContentService.GetBlueprintById(blueprintId);
            if (blueprint == null)
            {
                throw new HttpResponseException(HttpStatusCode.NotFound);
            }

            blueprint.Id = 0;
            blueprint.Name = string.Empty;
            blueprint.ParentId = parentId;

            var mapped = Mapper.Map<ContentItemDisplay>(blueprint);

            //remove the listview app if it exists
            mapped.ContentApps = mapped.ContentApps.Where(x => x.Alias != "childItems").ToList();

            return mapped;
        }

        /// <summary>
        /// Gets the Url for a given node ID
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public HttpResponseMessage GetNiceUrl(int id)
        {
            var url = Umbraco.Url(id);
            var response = Request.CreateResponse(HttpStatusCode.OK);
            response.Content = new StringContent(url, Encoding.UTF8, "text/plain");
            return response;
        }

        /// <summary>
        /// Gets the Url for a given node ID
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public HttpResponseMessage GetNiceUrl(Guid id)
        {
            var url = Umbraco.UrlProvider.GetUrl(id);
            var response = Request.CreateResponse(HttpStatusCode.OK);
            response.Content = new StringContent(url, Encoding.UTF8, "text/plain");
            return response;
        }

        /// <summary>
        /// Gets the Url for a given node ID
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public HttpResponseMessage GetNiceUrl(Udi id)
        {
            var guidUdi = id as GuidUdi;
            if (guidUdi != null)
            {
                return GetNiceUrl(guidUdi.Guid);
            }
            throw new HttpResponseException(HttpStatusCode.NotFound);
        }

        /// <summary>
        /// Gets the children for the content id passed in
        /// </summary>
        /// <returns></returns>
        [FilterAllowedOutgoingContent(typeof(IEnumerable<ContentItemBasic<ContentPropertyBasic>>), "Items")]
        public PagedResult<ContentItemBasic<ContentPropertyBasic>> GetChildren(
                int id,
                int pageNumber = 0,  //TODO: This should be '1' as it's not the index
                int pageSize = 0,
                string orderBy = "SortOrder",
                Direction orderDirection = Direction.Ascending,
                bool orderBySystemField = true,
                string filter = "")
        {
            return GetChildren(id, null, pageNumber, pageSize, orderBy, orderDirection, orderBySystemField, filter);
        }

        /// <summary>
        /// Gets the children for the content id passed in
        /// </summary>
        /// <returns></returns>
        [FilterAllowedOutgoingContent(typeof(IEnumerable<ContentItemBasic<ContentPropertyBasic>>), "Items")]
        public PagedResult<ContentItemBasic<ContentPropertyBasic>> GetChildren(
                int id,
                string includeProperties,
                int pageNumber = 0,  //TODO: This should be '1' as it's not the index
                int pageSize = 0,
                string orderBy = "SortOrder",
                Direction orderDirection = Direction.Ascending,
                bool orderBySystemField = true,
                string filter = "")
        {
            long totalChildren;
            IContent[] children;
            if (pageNumber > 0 && pageSize > 0)
            {
                IQuery<IContent> queryFilter = null;
                if (filter.IsNullOrWhiteSpace() == false)
                {
                    //add the default text filter
                    queryFilter = SqlContext.Query<IContent>()
                        .Where(x => x.Name.Contains(filter));
                }

                children = Services.ContentService
                    .GetPagedChildren(
                        id, (pageNumber - 1), pageSize,
                        out totalChildren,
                        orderBy, orderDirection, orderBySystemField,
                        queryFilter).ToArray();
            }
            else
            {
                children = Services.ContentService.GetChildren(id).ToArray();
                totalChildren = children.Length;
            }

            if (totalChildren == 0)
            {
                return new PagedResult<ContentItemBasic<ContentPropertyBasic>>(0, 0, 0);
            }

            var pagedResult = new PagedResult<ContentItemBasic<ContentPropertyBasic>>(totalChildren, pageNumber, pageSize);
            pagedResult.Items = children.Select(content =>
                Mapper.Map<IContent, ContentItemBasic<ContentPropertyBasic>>(content,
                    opts =>
                    {
                        // if there's a list of property aliases to map - we will make sure to store this in the mapping context.
                        if (String.IsNullOrWhiteSpace(includeProperties) == false)
                        {
                            opts.Items["IncludeProperties"] = includeProperties.Split(new[] { ", ", "," }, StringSplitOptions.RemoveEmptyEntries);
                        }
                    }));

            return pagedResult;
        }

        /// <summary>
        /// Returns permissions for all nodes passed in for the current user
        /// TODO: This should be moved to the CurrentUserController?
        /// </summary>
        /// <param name="nodeIds"></param>
        /// <returns></returns>
        [HttpPost]
        public Dictionary<int, string[]> GetPermissions(int[] nodeIds)
        {
            var permissions = Services.UserService
                    .GetPermissions(Security.CurrentUser, nodeIds);

            var permissionsDictionary = new Dictionary<int, string[]>();
            foreach (var nodeId in nodeIds)
            {
                var aggregatePerms = permissions.GetAllPermissions(nodeId).ToArray();
                permissionsDictionary.Add(nodeId, aggregatePerms);
            }

            return permissionsDictionary;
        }

        /// <summary>
        /// Checks a nodes permission for the current user
        /// TODO: This should be moved to the CurrentUserController?
        /// </summary>
        /// <param name="permissionToCheck"></param>
        /// <param name="nodeId"></param>
        /// <returns></returns>
        [HttpGet]
        public bool HasPermission(string permissionToCheck, int nodeId)
        {
            var p = Services.UserService.GetPermissions(Security.CurrentUser, nodeId).GetAllPermissions();
            if (p.Contains(permissionToCheck.ToString(CultureInfo.InvariantCulture)))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Creates a blueprint from a content item
        /// </summary>
        /// <param name="contentId">The content id to copy</param>
        /// <param name="name">The name of the blueprint</param>
        /// <returns></returns>
        [HttpPost]
        public SimpleNotificationModel CreateBlueprintFromContent([FromUri]int contentId, [FromUri]string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Value cannot be null or whitespace.", "name");

            var content = Services.ContentService.GetById(contentId);
            if (content == null)
                throw new HttpResponseException(Request.CreateResponse(HttpStatusCode.NotFound));

            EnsureUniqueName(name, content, "name");

            var blueprint = Services.ContentService.CreateContentFromBlueprint(content, name, Security.GetUserId().ResultOr(0));

            Services.ContentService.SaveBlueprint(blueprint, Security.GetUserId().ResultOr(0));

            var notificationModel = new SimpleNotificationModel();
            notificationModel.AddSuccessNotification(
                Services.TextService.Localize("blueprints/createdBlueprintHeading"),
                Services.TextService.Localize("blueprints/createdBlueprintMessage", new[] { content.Name })
            );

            return notificationModel;
        }

        private void EnsureUniqueName(string name, IContent content, string modelName)
        {
            var existing = Services.ContentService.GetBlueprintsForContentTypes(content.ContentTypeId);
            if (existing.Any(x => x.Name == name && x.Id != content.Id))
            {
                ModelState.AddModelError(modelName, Services.TextService.Localize("blueprints/duplicateBlueprintMessage"));
                throw new HttpResponseException(Request.CreateValidationErrorResponse(ModelState));
            }
        }

        /// <summary>
        /// Saves content
        /// </summary>
        /// <returns></returns>
        [FileUploadCleanupFilter]
        [ContentSaveValidation]
        public ContentItemDisplay PostSaveBlueprint([ModelBinder(typeof(ContentItemBinder))] ContentItemSave contentItem)
        {
            var contentItemDisplay = PostSaveInternal(contentItem,
                content =>
                {
                    EnsureUniqueName(content.Name, content, "Name");

                    Services.ContentService.SaveBlueprint(contentItem.PersistedContent, Security.CurrentUser.Id);
                    //we need to reuse the underlying logic so return the result that it wants
                    return OperationResult.Succeed(new EventMessages());
                });
            SetupBlueprint(contentItemDisplay, contentItemDisplay.PersistedContent);

            return contentItemDisplay;
        }

        /// <summary>
        /// Saves content
        /// </summary>
        /// <returns></returns>
        [FileUploadCleanupFilter]
        [ContentSaveValidation]
        [OutgoingEditorModelEvent]
        public ContentItemDisplay PostSave([ModelBinder(typeof(ContentItemBinder))] ContentItemSave contentItem)
        {
            var contentItemDisplay = PostSaveInternal(contentItem, content => Services.ContentService.Save(contentItem.PersistedContent, Security.CurrentUser.Id));
            return contentItemDisplay;
        }

        private ContentItemDisplay PostSaveInternal(ContentItemSave contentItem, Func<IContent, OperationResult> saveMethod)
        {
            //If we've reached here it means:
            // * Our model has been bound
            // * and validated
            // * any file attachments have been saved to their temporary location for us to use
            // * we have a reference to the DTO object and the persisted object
            // * Permissions are valid
            MapPropertyValues(contentItem);

            //We need to manually check the validation results here because:
            // * We still need to save the entity even if there are validation value errors
            // * Depending on if the entity is new, and if there are non property validation errors (i.e. the name is null)
            //      then we cannot continue saving, we can only display errors
            // * If there are validation errors and they were attempting to publish, we can only save, NOT publish and display
            //      a message indicating this
            if (ModelState.IsValid == false)
            {
                if (!RequiredForPersistenceAttribute.HasRequiredValuesForPersistence(contentItem) && IsCreatingAction(contentItem.Action))
                {
                    //ok, so the absolute mandatory data is invalid and it's new, we cannot actually continue!
                    // add the modelstate to the outgoing object and throw a validation message
                    var forDisplay = MapToDisplay(contentItem.PersistedContent);
                    forDisplay.Errors = ModelState.ToErrorDictionary();
                    throw new HttpResponseException(Request.CreateValidationErrorResponse(forDisplay));

                }

                //if the model state is not valid we cannot publish so change it to save
                switch (contentItem.Action)
                {
                    case ContentSaveAction.Publish:
                        contentItem.Action = ContentSaveAction.Save;
                        break;
                    case ContentSaveAction.PublishNew:
                        contentItem.Action = ContentSaveAction.SaveNew;
                        break;
                }
            }

            //initialize this to successful
            var publishStatus = new PublishResult(null, contentItem.PersistedContent);
            var wasCancelled = false;

            if (contentItem.Action == ContentSaveAction.Save || contentItem.Action == ContentSaveAction.SaveNew)
            {
                //save the item
                var saveResult = saveMethod(contentItem.PersistedContent);

                wasCancelled = saveResult.Success == false && saveResult.Result == OperationResultType.FailedCancelledByEvent;
            }
            else if (contentItem.Action == ContentSaveAction.SendPublish || contentItem.Action == ContentSaveAction.SendPublishNew)
            {
                var sendResult = Services.ContentService.SendToPublication(contentItem.PersistedContent, Security.CurrentUser.Id);
                wasCancelled = sendResult == false;
            }
            else
            {
                PublishInternal(contentItem, ref publishStatus, ref wasCancelled);
            }

            //get the updated model
            var display = MapToDisplay(contentItem.PersistedContent);

            //lasty, if it is not valid, add the modelstate to the outgoing object and throw a 403
            HandleInvalidModelState(display);

            //put the correct msgs in
            switch (contentItem.Action)
            {
                case ContentSaveAction.Save:
                case ContentSaveAction.SaveNew:
                    if (wasCancelled == false)
                    {
                        display.AddSuccessNotification(
                            Services.TextService.Localize("speechBubbles/editContentSavedHeader"),
                            Services.TextService.Localize("speechBubbles/editContentSavedText"));
                    }
                    else
                    {
                        AddCancelMessage(display);
                    }
                    break;
                case ContentSaveAction.SendPublish:
                case ContentSaveAction.SendPublishNew:
                    if (wasCancelled == false)
                    {
                        display.AddSuccessNotification(
                            Services.TextService.Localize("speechBubbles/editContentSendToPublish"),
                            Services.TextService.Localize("speechBubbles/editContentSendToPublishText"));
                    }
                    else
                    {
                        AddCancelMessage(display);
                    }
                    break;
                case ContentSaveAction.Publish:
                case ContentSaveAction.PublishNew:
                    ShowMessageForPublishStatus(publishStatus, display);
                    break;
            }

            //If the item is new and the operation was cancelled, we need to return a different
            // status code so the UI can handle it since it won't be able to redirect since there
            // is no Id to redirect to!
            if (wasCancelled && IsCreatingAction(contentItem.Action))
            {
                throw new HttpResponseException(Request.CreateValidationErrorResponse(display));
            }

            display.PersistedContent = contentItem.PersistedContent;

            return display;
        }

        /// <summary>
        /// Performs the publishing operation for a content item
        /// </summary>
        /// <param name="contentItem"></param>
        /// <param name="publishStatus"></param>
        /// <param name="wasCancelled"></param>
        /// <remarks>
        /// If this is a culture variant than we need to do some validation, if it's not we'll publish as normal
        /// </remarks>
        private void PublishInternal(ContentItemSave contentItem, ref PublishResult publishStatus, ref bool wasCancelled)
        {
            if (publishStatus == null) throw new ArgumentNullException(nameof(publishStatus));

            if (!contentItem.PersistedContent.ContentType.VariesByCulture())
            {
                //its invariant, proceed normally
                publishStatus = Services.ContentService.SaveAndPublish(contentItem.PersistedContent, userId: Security.CurrentUser.Id);
                wasCancelled = publishStatus.Result == PublishResultType.FailedCancelledByEvent;
            }
            else
            {
                var canPublish = true;

                //All variants in this collection should have a culture if we get here! but we'll double check and filter here
                var cultureVariants = contentItem.Variants.Where(x => !x.Culture.IsNullOrWhiteSpace()).ToList();

                //check if we are publishing other variants and validate them
                var allLangs = Services.LocalizationService.GetAllLanguages().ToDictionary(x => x.IsoCode, x => x, StringComparer.InvariantCultureIgnoreCase);
                
                //validate any mandatory variants that are not in the list
                var mandatoryLangs = Mapper.Map<IEnumerable<ILanguage>, IEnumerable<Language>>(allLangs.Values).Where(x => x.Mandatory);

                foreach (var lang in mandatoryLangs)
                {
                    //Check if a mandatory language is missing from being published
                    //fixme: This logic is wrong, we need to also check if this language doesn't already have a published version
                    if (cultureVariants.Any(x => x.Culture == lang.IsoCode && !x.Publish))
                    {
                        //cannot continue publishing since a required language that is not currently being published isn't published
                        if (!contentItem.PersistedContent.IsCulturePublished(lang.IsoCode))
                        {
                            var errMsg = Services.TextService.Localize("speechBubbles/contentReqCulturePublishError", new[] { allLangs[lang.IsoCode].CultureName });
                            ModelState.AddModelError("publish_variant_" + lang.IsoCode + "_", errMsg);
                            canPublish = false;
                        }
                    }
                }
                
                if (canPublish)
                {
                    //try to publish all the values on the model
                    canPublish = PublishCulture(contentItem.PersistedContent, cultureVariants, allLangs);
                }

                if (canPublish)
                {
                    //proceed to publish if all validation still succeeds
                    publishStatus = Services.ContentService.SavePublishing(contentItem.PersistedContent, Security.CurrentUser.Id);
                    wasCancelled = publishStatus.Result == PublishResultType.FailedCancelledByEvent;
                }
                else
                {
                    //can only save
                    var saveResult = Services.ContentService.Save(contentItem.PersistedContent, Security.CurrentUser.Id);
                    publishStatus = new PublishResult(PublishResultType.FailedCannotPublish, null, contentItem.PersistedContent);
                    wasCancelled = saveResult.Result == OperationResultType.FailedCancelledByEvent;
                }
            }
        }

        /// <summary>
        /// This will call PublishCulture on the content item for each culture that needs to be published including the invariant culture
        /// </summary>
        /// <param name="persistentContent"></param>
        /// <param name="cultureVariants"></param>
        /// <param name="allLangs"></param>
        /// <returns></returns>
        private bool PublishCulture(IContent persistentContent, IEnumerable<ContentVariantSave> cultureVariants, IDictionary<string, ILanguage> allLangs)
        {
            foreach(var variant in cultureVariants.Where(x => x.Publish))
            {
                // publishing any culture, implies the invariant culture
                var valid = persistentContent.PublishCulture(variant.Culture);
                if (!valid)
                {
                    AddCultureValidationError(variant.Culture, allLangs);
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Adds a generic culture error for use in displaying the culture validation error in the save/publish dialogs
        /// </summary>
        /// <param name="culture"></param>
        /// <param name="allLangs"></param>
        private void AddCultureValidationError(string culture, IDictionary<string, ILanguage> allLangs)
        {
            var key = "publish_variant_" + culture + "_";
            if (ModelState.ContainsKey(key)) return;
            var errMsg = Services.TextService.Localize("speechBubbles/contentCultureValidationError", new[] { allLangs[culture].CultureName });
            ModelState.AddModelError(key, errMsg);
        }

        /// <summary>
        /// Publishes a document with a given ID
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        /// <remarks>
        /// The CanAccessContentAuthorize attribute will deny access to this method if the current user
        /// does not have Publish access to this node.
        /// </remarks>
        ///
        [EnsureUserPermissionForContent("id", 'U')]
        public HttpResponseMessage PostPublishById(int id)
        {
            var foundContent = GetObjectFromRequest(() => Services.ContentService.GetById(id));

            if (foundContent == null)
            {
                return HandleContentNotFound(id, false);
            }

            var publishResult = Services.ContentService.SavePublishing(foundContent, Security.GetUserId().ResultOr(0));
            if (publishResult.Success == false)
            {
                var notificationModel = new SimpleNotificationModel();
                ShowMessageForPublishStatus(publishResult, notificationModel);
                return Request.CreateValidationErrorResponse(notificationModel);
            }

            //return ok
            return Request.CreateResponse(HttpStatusCode.OK);

        }

        [HttpDelete]
        [HttpPost]
        public HttpResponseMessage DeleteBlueprint(int id)
        {
            var found = Services.ContentService.GetBlueprintById(id);

            if (found == null)
            {
                return HandleContentNotFound(id, false);
            }

            Services.ContentService.DeleteBlueprint(found);

            return Request.CreateResponse(HttpStatusCode.OK);
        }

        /// <summary>
        /// Moves an item to the recycle bin, if it is already there then it will permanently delete it
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        /// <remarks>
        /// The CanAccessContentAuthorize attribute will deny access to this method if the current user
        /// does not have Delete access to this node.
        /// </remarks>
        [EnsureUserPermissionForContent("id", 'D')]
        [HttpDelete]
        [HttpPost]
        public HttpResponseMessage DeleteById(int id)
        {
            var foundContent = GetObjectFromRequest(() => Services.ContentService.GetById(id));

            if (foundContent == null)
            {
                return HandleContentNotFound(id, false);
            }

            //if the current item is in the recycle bin
            if (foundContent.Trashed == false)
            {
                var moveResult = Services.ContentService.MoveToRecycleBin(foundContent, Security.GetUserId().ResultOr(0));
                if (moveResult.Success == false)
                {
                    //returning an object of INotificationModel will ensure that any pending
                    // notification messages are added to the response.
                    return Request.CreateValidationErrorResponse(new SimpleNotificationModel());
                }
            }
            else
            {
                var deleteResult = Services.ContentService.Delete(foundContent, Security.GetUserId().ResultOr(0));
                if (deleteResult.Success == false)
                {
                    //returning an object of INotificationModel will ensure that any pending
                    // notification messages are added to the response.
                    return Request.CreateValidationErrorResponse(new SimpleNotificationModel());
                }
            }

            return Request.CreateResponse(HttpStatusCode.OK);
        }

        /// <summary>
        /// Empties the recycle bin
        /// </summary>
        /// <returns></returns>
        /// <remarks>
        /// attributed with EnsureUserPermissionForContent to verify the user has access to the recycle bin
        /// </remarks>
        [HttpDelete]
        [HttpPost]
        [EnsureUserPermissionForContent(Constants.System.RecycleBinContent)]
        public HttpResponseMessage EmptyRecycleBin()
        {
            Services.ContentService.EmptyRecycleBin();

            return Request.CreateNotificationSuccessResponse(Services.TextService.Localize("defaultdialogs/recycleBinIsEmpty"));
        }

        /// <summary>
        /// Change the sort order for content
        /// </summary>
        /// <param name="sorted"></param>
        /// <returns></returns>
        [EnsureUserPermissionForContent("sorted.ParentId", 'S')]
        public HttpResponseMessage PostSort(ContentSortOrder sorted)
        {
            if (sorted == null)
            {
                return Request.CreateResponse(HttpStatusCode.NotFound);
            }

            //if there's nothing to sort just return ok
            if (sorted.IdSortOrder.Length == 0)
            {
                return Request.CreateResponse(HttpStatusCode.OK);
            }

            try
            {
                var contentService = Services.ContentService;

                // Save content with new sort order and update content xml in db accordingly
                if (contentService.Sort(sorted.IdSortOrder) == false)
                {
                    Logger.Warn<ContentController>("Content sorting failed, this was probably caused by an event being cancelled");
                    return Request.CreateValidationErrorResponse("Content sorting failed, this was probably caused by an event being cancelled");
                }

                if (sorted.ParentId > 0)
                {
                    Services.NotificationService.SendNotification(contentService.GetById(sorted.ParentId), ActionSort.Instance, UmbracoContext, Services.TextService, GlobalSettings);
                }

                return Request.CreateResponse(HttpStatusCode.OK);
            }
            catch (Exception ex)
            {
                Logger.Error<ContentController>("Could not update content sort order", ex);
                throw;
            }
        }

        /// <summary>
        /// Change the sort order for media
        /// </summary>
        /// <param name="move"></param>
        /// <returns></returns>
        [EnsureUserPermissionForContent("move.ParentId", 'M')]
        public HttpResponseMessage PostMove(MoveOrCopy move)
        {
            var toMove = ValidateMoveOrCopy(move);

            Services.ContentService.Move(toMove, move.ParentId, Security.GetUserId().ResultOr(0));

            var response = Request.CreateResponse(HttpStatusCode.OK);
            response.Content = new StringContent(toMove.Path, Encoding.UTF8, "text/plain");
            return response;
        }

        /// <summary>
        /// Copies a content item and places the copy as a child of a given parent Id
        /// </summary>
        /// <param name="copy"></param>
        /// <returns></returns>
        [EnsureUserPermissionForContent("copy.ParentId", 'C')]
        public HttpResponseMessage PostCopy(MoveOrCopy copy)
        {
            var toCopy = ValidateMoveOrCopy(copy);

            var c = Services.ContentService.Copy(toCopy, copy.ParentId, copy.RelateToOriginal, copy.Recursive, Security.GetUserId().ResultOr(0));

            var response = Request.CreateResponse(HttpStatusCode.OK);
            response.Content = new StringContent(c.Path, Encoding.UTF8, "text/plain");
            return response;
        }

        /// <summary>
        /// Unpublishes a node with a given Id and returns the unpublished entity
        /// </summary>
        /// <param name="id">The content id to unpublish</param>
        /// <param name="id">The culture variant for the content id to unpublish, if none specified will unpublish all variants of the content</param>
        /// <returns></returns>
        [EnsureUserPermissionForContent("id", 'U')]
        [OutgoingEditorModelEvent]
        public ContentItemDisplay PostUnPublish(int id, string culture = null)
        {
            var foundContent = GetObjectFromRequest(() => Services.ContentService.GetById(id));

            if (foundContent == null)
                HandleContentNotFound(id);

            var unpublishResult = Services.ContentService.Unpublish(foundContent, culture: culture, userId: Security.GetUserId().ResultOr(0));

            var content = MapToDisplay(foundContent);

            if (!unpublishResult.Success)
            {
                AddCancelMessage(content);
                throw new HttpResponseException(Request.CreateValidationErrorResponse(content));
            }
            else
            {
                //fixme should have a better localized method for when we have the UnpublishResultType.SuccessMandatoryCulture status

                content.AddSuccessNotification(
                    Services.TextService.Localize("content/unPublish"),
                    unpublishResult.Result == UnpublishResultType.SuccessCulture
                        ? Services.TextService.Localize("speechBubbles/contentVariationUnpublished", new[] { culture })
                        : Services.TextService.Localize("speechBubbles/contentUnpublished"));

                return content;
            }
        }

        [HttpPost]
        public DomainSave PostSaveLanguageAndDomains(DomainSave model)
        {
            var node = Services.ContentService.GetById(model.NodeId);

            if (node == null)
            {
                var response = Request.CreateResponse(HttpStatusCode.BadRequest);
                response.Content = new StringContent($"There is no content node with id {model.NodeId}.");
                response.ReasonPhrase = "Node Not Found.";
                throw new HttpResponseException(response);
            }

            var permission = Services.UserService.GetPermissions(Security.CurrentUser, node.Path);

            if (permission.AssignedPermissions.Contains(ActionAssignDomain.Instance.Letter.ToString(), StringComparer.Ordinal) == false)
            {
                var response = Request.CreateResponse(HttpStatusCode.BadRequest);
                response.Content = new StringContent("You do not have permission to assign domains on that node.");
                response.ReasonPhrase = "Permission Denied.";
                throw new HttpResponseException(response);
            }

            model.Valid = true;
            var domains = Services.DomainService.GetAssignedDomains(model.NodeId, true).ToArray();
            var languages = Services.LocalizationService.GetAllLanguages().ToArray();
            var language = model.Language > 0 ? languages.FirstOrDefault(l => l.Id == model.Language) : null;

            // process wildcard
            if (language != null)
            {
                // yet there is a race condition here...
                var wildcard = domains.FirstOrDefault(d => d.IsWildcard);
                if (wildcard != null)
                {
                    wildcard.LanguageId = language.Id;
                }
                else
                {
                    wildcard = new UmbracoDomain("*" + model.NodeId)
                    {
                        LanguageId = model.Language,
                        RootContentId = model.NodeId
                    };
                }

                var saveAttempt = Services.DomainService.Save(wildcard);
                if (saveAttempt == false)
                {
                    var response = Request.CreateResponse(HttpStatusCode.BadRequest);
                    response.Content = new StringContent("Saving domain failed");
                    response.ReasonPhrase = saveAttempt.Result.Result.ToString();
                    throw new HttpResponseException(response);
                }
            }
            else
            {
                var wildcard = domains.FirstOrDefault(d => d.IsWildcard);
                if (wildcard != null)
                {
                    Services.DomainService.Delete(wildcard);
                }
            }

            // process domains
            // delete every (non-wildcard) domain, that exists in the DB yet is not in the model
            foreach (var domain in domains.Where(d => d.IsWildcard == false && model.Domains.All(m => m.Name.InvariantEquals(d.DomainName) == false)))
            {
                Services.DomainService.Delete(domain);
            }
            
            var names = new List<string>();

            // create or update domains in the model
            foreach (var domainModel in model.Domains.Where(m => string.IsNullOrWhiteSpace(m.Name) == false))
            {
                language = languages.FirstOrDefault(l => l.Id == domainModel.Lang);
                if (language == null)
                {
                    continue;
                }

                var name = domainModel.Name.ToLowerInvariant();
                if (names.Contains(name))
                {
                    domainModel.Duplicate = true;
                    continue;
                }
                names.Add(name);
                var domain = domains.FirstOrDefault(d => d.DomainName.InvariantEquals(domainModel.Name));
                if (domain != null)
                {
                    domain.LanguageId = language.Id;
                    Services.DomainService.Save(domain);
                }
                else if (Services.DomainService.Exists(domainModel.Name))
                {
                    domainModel.Duplicate = true;
                    var xdomain = Services.DomainService.GetByName(domainModel.Name);
                    var xrcid = xdomain.RootContentId;
                    if (xrcid.HasValue)
                    {
                        var xcontent = Services.ContentService.GetById(xrcid.Value);
                        var xnames = new List<string>();
                        while (xcontent != null)
                        {
                            xnames.Add(xcontent.Name);
                            if (xcontent.ParentId < -1)
                                xnames.Add("Recycle Bin");
                            xcontent = xcontent.Parent(Services.ContentService);
                        }
                        xnames.Reverse();
                        domainModel.Other = "/" + string.Join("/", xnames);
                    }
                }
                else
                {
                    // yet there is a race condition here...
                    var newDomain = new UmbracoDomain(name)
                    {
                        LanguageId = domainModel.Lang,
                        RootContentId = model.NodeId
                    };
                    var saveAttempt = Services.DomainService.Save(newDomain);
                    if (saveAttempt == false)
                    {
                        var response = Request.CreateResponse(HttpStatusCode.BadRequest);
                        response.Content = new StringContent("Saving new domain failed");
                        response.ReasonPhrase = saveAttempt.Result.Result.ToString();
                        throw new HttpResponseException(response);
                    }
                }
            }

            model.Valid = model.Domains.All(m => m.Duplicate == false);

            return model;
        }

        /// <summary>
        /// Override to ensure there is culture specific errors in the result if any errors are for culture properties
        /// </summary>
        /// <param name="display"></param>
        /// <remarks>
        /// This is required to wire up the validation in the save/publish dialog
        /// </remarks>
        protected override void HandleInvalidModelState(IErrorModel display)
        {
            if (!ModelState.IsValid)
            {
                //Add any culture specific errors here
                var cultureErrors = ModelState.Keys
                    .Select(x => x.Split('.')) //split into parts
                    .Where(x => x.Length >= 3 && x[0] == "_Properties") //only choose _Properties errors
                    .Select(x => x[2]) //select the culture part
                    .Where(x => !x.IsNullOrWhiteSpace()) //if it has a value
                    .Distinct()
                    .ToList();

                var allLangs = Services.LocalizationService.GetAllLanguages().ToDictionary(x => x.IsoCode, x => x, StringComparer.InvariantCultureIgnoreCase);

                foreach (var cultureError in cultureErrors)
                {
                    AddCultureValidationError(cultureError, allLangs);
                }
            }
                
            base.HandleInvalidModelState(display);
        }

        /// <summary>
        /// Maps the dto property values to the persisted model
        /// </summary>
        /// <param name="contentSave"></param>
        private void MapPropertyValues(ContentItemSave contentSave)
        {
            //inline method to determine if a property type varies
            bool Varies(Property property) => property.PropertyType.VariesByCulture();

            //loop through each variant, set the correct name and property values
            foreach (var variant in contentSave.Variants)
            {
                //Don't update anything for this variant if Save is not true
                if (!variant.Save) continue;

                //Don't update the name if it is empty
                if (!variant.Name.IsNullOrWhiteSpace())
                {
                    if (contentSave.PersistedContent.ContentType.VariesByCulture())
                    {
                        if (variant.Culture.IsNullOrWhiteSpace())
                            throw new InvalidOperationException($"Cannot set culture name without a culture.");
                        contentSave.PersistedContent.SetCultureName(variant.Name, variant.Culture);
                    }
                    else
                    {
                        contentSave.PersistedContent.Name = variant.Name;
                    }
                }

                //for each variant, map the property values
                MapPropertyValuesForPersistence<IContent, ContentItemSave>(
                    contentSave,
                    variant.PropertyCollectionDto,
                    (save, property) => Varies(property) ? property.GetValue(variant.Culture) : property.GetValue(),         //get prop val
                    (save, property, v) => { if (Varies(property)) property.SetValue(v, variant.Culture); else property.SetValue(v); });  //set prop val
            }

            //TODO: We need to support 'send to publish'

            contentSave.PersistedContent.ExpireDate = contentSave.ExpireDate;
            contentSave.PersistedContent.ReleaseDate = contentSave.ReleaseDate;

            //only set the template if it didn't change
            var templateChanged = (contentSave.PersistedContent.Template == null && contentSave.TemplateAlias.IsNullOrWhiteSpace() == false)
                                                        || (contentSave.PersistedContent.Template != null && contentSave.PersistedContent.Template.Alias != contentSave.TemplateAlias)
                                                        || (contentSave.PersistedContent.Template != null && contentSave.TemplateAlias.IsNullOrWhiteSpace());
            if (templateChanged)
            {
                var template = Services.FileService.GetTemplate(contentSave.TemplateAlias);
                if (template == null && contentSave.TemplateAlias.IsNullOrWhiteSpace() == false)
                {
                    //ModelState.AddModelError("Template", "No template exists with the specified alias: " + contentItem.TemplateAlias);
                    Logger.Warn<ContentController>("No template exists with the specified alias: " + contentSave.TemplateAlias);
                }
                else
                {
                    //NOTE: this could be null if there was a template and the posted template is null, this should remove the assigned template
                    contentSave.PersistedContent.Template = template;
                }
            }
        }

        /// <summary>
        /// Ensures the item can be moved/copied to the new location
        /// </summary>
        /// <param name="model"></param>
        /// <returns></returns>
        private IContent ValidateMoveOrCopy(MoveOrCopy model)
        {
            if (model == null)
            {
                throw new HttpResponseException(HttpStatusCode.NotFound);
            }

            var contentService = Services.ContentService;
            var toMove = contentService.GetById(model.Id);
            if (toMove == null)
            {
                throw new HttpResponseException(HttpStatusCode.NotFound);
            }
            if (model.ParentId < 0)
            {
                //cannot move if the content item is not allowed at the root
                if (toMove.ContentType.AllowedAsRoot == false)
                {
                    throw new HttpResponseException(
                            Request.CreateNotificationValidationErrorResponse(
                                    Services.TextService.Localize("moveOrCopy/notAllowedAtRoot")));
                }
            }
            else
            {
                var parent = contentService.GetById(model.ParentId);
                if (parent == null)
                {
                    throw new HttpResponseException(HttpStatusCode.NotFound);
                }

                //check if the item is allowed under this one
                if (parent.ContentType.AllowedContentTypes.Select(x => x.Id).ToArray()
                        .Any(x => x.Value == toMove.ContentType.Id) == false)
                {
                    throw new HttpResponseException(
                            Request.CreateNotificationValidationErrorResponse(
                                    Services.TextService.Localize("moveOrCopy/notAllowedByContentType")));
                }

                // Check on paths
                if ((string.Format(",{0},", parent.Path)).IndexOf(string.Format(",{0},", toMove.Id), StringComparison.Ordinal) > -1)
                {
                    throw new HttpResponseException(
                            Request.CreateNotificationValidationErrorResponse(
                                    Services.TextService.Localize("moveOrCopy/notAllowedByPath")));
                }
            }

            return toMove;
        }

        private void ShowMessageForPublishStatus(PublishResult status, INotificationModel display)
        {
            switch (status.Result)
            {
                case PublishResultType.Success:
                case PublishResultType.SuccessAlready:
                    display.AddSuccessNotification(
                            Services.TextService.Localize("speechBubbles/editContentPublishedHeader"),
                            Services.TextService.Localize("speechBubbles/editContentPublishedText"));
                    break;
                case PublishResultType.FailedPathNotPublished:
                    display.AddWarningNotification(
                            Services.TextService.Localize("publish"),
                            Services.TextService.Localize("publish/contentPublishedFailedByParent",
                                new[] { $"{status.Content.Name} ({status.Content.Id})" }).Trim());
                    break;
                case PublishResultType.FailedCancelledByEvent:
                    AddCancelMessage(display, "publish", "speechBubbles/contentPublishedFailedByEvent");
                    break;
                case PublishResultType.FailedAwaitingRelease:
                    display.AddWarningNotification(
                            Services.TextService.Localize("publish"),
                            Services.TextService.Localize("publish/contentPublishedFailedAwaitingRelease",
                                new[] { $"{status.Content.Name} ({status.Content.Id})" }).Trim());
                    break;
                case PublishResultType.FailedHasExpired:
                    display.AddWarningNotification(
                            Services.TextService.Localize("publish"),
                            Services.TextService.Localize("publish/contentPublishedFailedExpired",
                                new[] { $"{status.Content.Name} ({status.Content.Id})", }).Trim());
                    break;
                case PublishResultType.FailedIsTrashed:
                    display.AddWarningNotification(
                        Services.TextService.Localize("publish"),
                        "publish/contentPublishedFailedIsTrashed"); // fixme properly localize!
                    break;
                case PublishResultType.FailedContentInvalid:
                    display.AddWarningNotification(
                            Services.TextService.Localize("publish"),
                            Services.TextService.Localize("publish/contentPublishedFailedInvalid",
                                new[]
                                {
                                    $"{status.Content.Name} ({status.Content.Id})",
                                    string.Join(",", status.InvalidProperties.Select(x => x.Alias))
                                }).Trim());
                    break;
                case PublishResultType.FailedByCulture:
                    display.AddWarningNotification(
                        Services.TextService.Localize("publish"),
                        "publish/contentPublishedFailedByCulture"); // fixme properly localize!
                    break;
                default:
                    throw new IndexOutOfRangeException($"PublishedResultType \"{status.Result}\" was not expected.");
            }
        }

        /// <summary>
        /// Performs a permissions check for the user to check if it has access to the node based on
        /// start node and/or permissions for the node
        /// </summary>
        /// <param name="storage">The storage to add the content item to so it can be reused</param>
        /// <param name="user"></param>
        /// <param name="userService"></param>
        /// <param name="contentService"></param>
        /// <param name="entityService"></param>
        /// <param name="nodeId">The content to lookup, if the contentItem is not specified</param>
        /// <param name="permissionsToCheck"></param>
        /// <param name="contentItem">Specifies the already resolved content item to check against</param>
        /// <returns></returns>
        internal static bool CheckPermissions(
                IDictionary<string, object> storage,
                IUser user,
                IUserService userService,
                IContentService contentService,
                IEntityService entityService,
                int nodeId,
                char[] permissionsToCheck = null,
                IContent contentItem = null)
        {
            if (storage == null) throw new ArgumentNullException("storage");
            if (user == null) throw new ArgumentNullException("user");
            if (userService == null) throw new ArgumentNullException("userService");
            if (contentService == null) throw new ArgumentNullException("contentService");
            if (entityService == null) throw new ArgumentNullException("entityService");

            if (contentItem == null && nodeId != Constants.System.Root && nodeId != Constants.System.RecycleBinContent)
            {
                contentItem = contentService.GetById(nodeId);
                //put the content item into storage so it can be retreived
                // in the controller (saves a lookup)
                storage[typeof(IContent).ToString()] = contentItem;
            }

            if (contentItem == null && nodeId != Constants.System.Root && nodeId != Constants.System.RecycleBinContent)
            {
                throw new HttpResponseException(HttpStatusCode.NotFound);
            }

            var hasPathAccess = (nodeId == Constants.System.Root)
                ? user.HasContentRootAccess(entityService)
                : (nodeId == Constants.System.RecycleBinContent)
                    ? user.HasContentBinAccess(entityService)
                    : user.HasPathAccess(contentItem, entityService);

            if (hasPathAccess == false)
            {
                return false;
            }

            if (permissionsToCheck == null || permissionsToCheck.Length == 0)
            {
                return true;
            }

            //get the implicit/inherited permissions for the user for this path,
            //if there is no content item for this id, than just use the id as the path (i.e. -1 or -20)
            var path = contentItem != null ? contentItem.Path : nodeId.ToString();
            var permission = userService.GetPermissionsForPath(user, path);

            var allowed = true;
            foreach (var p in permissionsToCheck)
            {
                if (permission == null
                    || permission.GetAllPermissions().Contains(p.ToString(CultureInfo.InvariantCulture)) == false)
                {
                    allowed = false;
                }
            }
            return allowed;
        }

        /// <summary>
        /// Used to map an <see cref="IContent"/> instance to a <see cref="ContentItemDisplay"/> and ensuring a language is present if required
        /// </summary>
        /// <param name="content"></param>
        /// <param name="culture"></param>
        /// <returns></returns>
        private ContentItemDisplay MapToDisplay(IContent content)
        {
            var display = Mapper.Map<ContentItemDisplay>(content);
            return display;
        }
		
        [EnsureUserPermissionForContent("contentId", 'R')]
        public IEnumerable<NotifySetting> GetNotificationOptions(int contentId)
        {
            var notifications = new List<NotifySetting>();
            if (contentId <= 0) throw new HttpResponseException(Request.CreateResponse(HttpStatusCode.NotFound));

            var content = Services.ContentService.GetById(contentId);
            if (content == null) throw new HttpResponseException(Request.CreateResponse(HttpStatusCode.NotFound));

            var userNotifications = Services.NotificationService.GetUserNotifications(Security.CurrentUser, content.Path).ToList();

            foreach (var a in Current.Actions.Where(x => x.ShowInNotifier))
            {
                var n = new NotifySetting
                {
                    Name = Services.TextService.Localize("actions", a.Alias),
                    Checked = userNotifications.FirstOrDefault(x=> x.Action == a.Letter.ToString()) != null,
                    NotifyCode = a.Letter.ToString()
                };
                notifications.Add(n);
            }

            return notifications;
        }

        public void PostNotificationOptions(int contentId, [FromUri] string[] notifyOptions)
        {
            if (contentId <= 0) throw new HttpResponseException(Request.CreateResponse(HttpStatusCode.NotFound));
            var content = Services.ContentService.GetById(contentId);
            if (content == null) throw new HttpResponseException(Request.CreateResponse(HttpStatusCode.NotFound));

            Services.NotificationService.SetNotifications(Security.CurrentUser, content, notifyOptions);
        }

        [HttpGet]
        public AvailableContentTypes GetAvailableContentTypesToChangeTo(int currentNodeId)
        {
            var content = Services.ContentService.GetById(currentNodeId);

            // Start with all content types
            var contentTypes = Services.ContentTypeService.GetAll().ToArray();

            //Remove invalid ones from list of potential alternatives
            //Remove CurrentDoctype
            contentTypes = contentTypes.Where(x => x.Id != content.ContentTypeId).ToArray();

            //Remove Invalid Parent Doctypes
            if (content.ParentId == -1)
            {
                // Root content, only include those that have been selected as allowed at root
                contentTypes = contentTypes.Where(x => x.AllowedAsRoot).ToArray();
            }
            else
            {
                // Below root, so only include those allowed as sub-nodes for the parent
                var parentNode = Services.ContentService.GetById(content.ParentId);

                contentTypes = contentTypes
                    .Where(x => parentNode.ContentType.AllowedContentTypes
                        .Select(y => y.Id.Value)
                        .Contains(x.Id)).ToArray();
            }

            //Remove invalid children doctypes
            var docTypeIdsOfChildren = content.Children(Services.ContentService)
                .Select(x => x.ContentType.Id)
                .Distinct()
                .ToList();

            contentTypes = contentTypes
                .Where(x => x.AllowedContentTypes
                    .Select(y => y.Id.Value)
                    .ContainsAll(docTypeIdsOfChildren)).ToArray();

            //Return a friendlier model down the wire
            var basicContentTypes = new List<ContentTypeBasic>();
            foreach (var type in contentTypes)
            {
                basicContentTypes.Add(Mapper.Map<IContentType, ContentTypeBasic>(type));
            }

            return new AvailableContentTypes
            {
                ContentTypes = basicContentTypes,
                CurrentNodeName = content.Name,
                CurrentContentType =  Mapper.Map<IContentType, ContentTypeBasic>(content.ContentType)
            };
        }

        [HttpGet]
        public AvailableProperties GetAvailableProperties(string fromPropertyAlias, string toPropertyAlias)
        {
            //Get the new property type we are changing to
            var newContentType = Services.ContentTypeService.Get(toPropertyAlias);

            //Get the current property type that the content node is using
            var currentContentType = Services.ContentTypeService.Get(fromPropertyAlias);

            var properties = new List<CurrentProperty>();

            //Create a list of current properties the current doctype has
            //With each property in this list specifying what new properties from the doctype we are changing to
            //Ensuring they use the same underlying property editor alias
            foreach (var currentProp in currentContentType.PropertyTypes)
            {
                var propertyToAdd = new CurrentProperty
                {
                    Name = currentProp.Name,
                    Alias = currentProp.Alias
                };

                var allowedProps = new List<NewProperty>();
                foreach (var newProp in newContentType.PropertyTypes.Where(x => x.PropertyEditorAlias == currentProp.PropertyEditorAlias))
                {
                    var allowedProp = new NewProperty
                    {
                        Name = newProp.Name,
                        Alias = newProp.Alias
                    };

                    allowedProps.Add(allowedProp);
                }

                propertyToAdd.Allowed = allowedProps;
                properties.Add(propertyToAdd);
            }

            var templates = new List<TemplateDisplay>();
            foreach (var template in newContentType.AllowedTemplates)
            {
                templates.Add(Mapper.Map<ITemplate, TemplateDisplay>(template));
            }

            return new AvailableProperties
            {
                Templates = templates,
                CurrentProperties = properties
            };
        }

        /*
            {
              "contentNodeId": 1234,
              "newContentTypeId": 80,
              "newTemplateId": 40,
              "fieldMap": [
                {
                  "fromAlias": "siteTitle",
                  "toAlias": "newsTitle"
                },
                {
                  "fromAlias": "bodyText",
                  "toAlias": null
                }
              ]
            }
         */
        [HttpPost]
        public HttpResponseMessage PostContentTypeChange(ChangeContentType model)
        {
            var content = Services.ContentService.GetById(model.ContentNodeId);
            if (content == null)
            {
                //throw some ex
                return Request.CreateNotificationValidationErrorResponse("Content is null");
            }

            //Check that field mappings (do not have something selected twice & is unique)
            if (model.FieldMap.GroupBy(x => x.ToAlias).Any(g => g.Count() > 1))
            {
                //Throw Error
                return Request.CreateNotificationValidationErrorResponse(Services.TextService.Localize("changeDocType/validationErrorPropertyWithMoreThanOneMapping"));
            }
            
            // For all properties to be mapped, save the current values to a temporary list
            var propertyMappings = new List<FieldMapValue>();
            foreach (var map in model.FieldMap)
            {
                //If the ToAlias is not empty
                if (!string.IsNullOrEmpty(map.ToAlias))
                {
                    // Mapping property, get current property value from alias
                    var sourceAlias = map.FromAlias;
                    var sourcePropertyValue = content.GetValue(sourceAlias);

                    // Add to list
                    propertyMappings.Add(new FieldMapValue
                    {
                        ToAlias = map.ToAlias,
                        CurrentValue = sourcePropertyValue
                    });
                }
            }

            // Get flag for if content already published
            var wasPublished = content.Published;

            // Change the document type passing flag to clear the properties
            var newContentType = Services.ContentTypeService.Get(model.NewContentTypeId);
            content.ChangeContentType(newContentType, true);

            //Set the template if one has been selected
            content.Template = model.NewTemplateId > 0 ? Services.FileService.GetTemplate(model.NewTemplateId) : null;
            
            // Port across the property values to the new properties
            foreach (var propertyMapping in propertyMappings)
            {
                content.SetValue(propertyMapping.ToAlias, propertyMapping.CurrentValue);
            }

            // Save the changes
            var user = Security.CurrentUser;
            Services.ContentService.Save(content, user.Id);

            // Publish if the content was already published
            if (wasPublished)
            {
                // no values to publish, really
                Services.ContentService.SaveAndPublish(content, userId: user.Id);
            }

            //All OK - return a 200
            return Request.CreateNotificationSuccessResponse("DOCTYPE CHANGED");
        }

    }
}

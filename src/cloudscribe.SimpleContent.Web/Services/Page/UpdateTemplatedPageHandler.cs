﻿// Copyright (c) Source Tree Solutions, LLC. All rights reserved.
// Author:                  Joe Audette
// Created:                 2018-06-22
// Last Modified:           2018-11-13
// 

using cloudscribe.DateTimeUtils;
using cloudscribe.SimpleContent.Models;
using cloudscribe.SimpleContent.Models.Versioning;
using cloudscribe.SimpleContent.Web.Templating;
using cloudscribe.Web.Common.Razor;
using MediatR;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace cloudscribe.SimpleContent.Web.Services
{
    public class UpdateTemplatedPageHandler : IRequestHandler<UpdateTemplatedPageRequest, CommandResult<IPage>>
    {
        public UpdateTemplatedPageHandler(
            IProjectService projectService,
            IPageService pageService,
            IContentHistoryCommands historyCommands,
            ITimeZoneHelper timeZoneHelper,
            ITimeZoneIdResolver timeZoneIdResolver,
            IEnumerable<IModelSerializer> serializers,
            IEnumerable<IParseModelFromForm> formParsers,
            IEnumerable<IValidateTemplateModel> modelValidators,
            ViewRenderer viewRenderer,
            IStringLocalizer<cloudscribe.SimpleContent.Web.SimpleContent> localizer,
            ILogger<UpdateTemplatedPageHandler> logger
            )
        {
            _projectService = projectService;
            _pageService = pageService;
            _historyCommands = historyCommands;
            _timeZoneHelper = timeZoneHelper;
            _timeZoneIdResolver = timeZoneIdResolver;
            _serializers = serializers;
            _formParsers = formParsers;
            _modelValidators = modelValidators;
            _viewRenderer = viewRenderer;
            _localizer = localizer;
            _log = logger;
        }

        private readonly IProjectService _projectService;
        private readonly IPageService _pageService;
        private readonly IContentHistoryCommands _historyCommands;
        private readonly ITimeZoneHelper _timeZoneHelper;
        private readonly ITimeZoneIdResolver _timeZoneIdResolver;
        private readonly IEnumerable<IModelSerializer> _serializers;
        private readonly IEnumerable<IParseModelFromForm> _formParsers;
        private readonly IEnumerable<IValidateTemplateModel> _modelValidators;
        private readonly ViewRenderer _viewRenderer;
        private readonly IStringLocalizer _localizer;
        private readonly ILogger _log;

        private IModelSerializer GetSerializer(string name)
        {
            foreach (var s in _serializers)
            {
                if (s.Name == name) return s;
            }

            return _serializers.FirstOrDefault();
        }

        private IParseModelFromForm GetFormParser(string name)
        {
            foreach (var s in _formParsers)
            {
                if (s.ParserName == name) return s;
            }

            return _formParsers.FirstOrDefault();
        }

        private IValidateTemplateModel GetValidator(string name)
        {
            foreach (var s in _modelValidators)
            {
                if (s.ValidatorName == name) return s;
            }

            return _modelValidators.FirstOrDefault();
        }

        public async Task<CommandResult<IPage>> Handle(UpdateTemplatedPageRequest request, CancellationToken cancellationToken = default(CancellationToken))
        {
            var errors = new List<string>();
            var customModelIsValid = true;
            try
            {
                var page = request.Page;
                var history = page.CreateHistory(request.UserName);
                var project = await _projectService.GetProjectSettings(request.ProjectId);
                var serializer = GetSerializer(request.Template.SerializerName);
                var parser = GetFormParser(request.Template.FormParserName);
                var validator = GetValidator(request.Template.ValidatorName);
                var type = Type.GetType(request.Template.ModelType);
                var model = parser.ParseModel(request.Template.ModelType, request.Form);
                if(model == null)
                {
                    errors.Add(_localizer["Failed to parse custom template model from form."]);
                    customModelIsValid = false;
                    //failed to parse model from form
                    // at least return the original model before changes
                    string pageModelString;
                    if (!string.IsNullOrWhiteSpace(page.DraftSerializedModel))
                    {
                        pageModelString = page.DraftSerializedModel;
                    }
                    else
                    {
                        pageModelString = page.SerializedModel;
                    }
                    if (!string.IsNullOrWhiteSpace(pageModelString))
                    {
                        request.ViewModel.TemplateModel = serializer.Deserialize(request.Template.ModelType, pageModelString);
                    }
                }

                if (customModelIsValid)
                {
                    // we are going to return the parsed model either way
                    request.ViewModel.TemplateModel = model;

                    var validationContext = new ValidationContext(model, serviceProvider: null, items: null);
                    var validationResults = new List<ValidationResult>();

                    customModelIsValid = validator.IsValid(model, validationContext, validationResults);
                    if (!customModelIsValid)
                    {
                        foreach(var item in validationResults)
                        {
                           foreach(var memberName in item.MemberNames)
                           {
                                request.ModelState.AddModelError(memberName, item.ErrorMessage);

                           }
                        }
                    }
                }

                if (!string.IsNullOrEmpty(request.ViewModel.Slug))
                {
                    // remove any bad characters
                    request.ViewModel.Slug = ContentUtils.CreateSlug(request.ViewModel.Slug);
                    if (request.ViewModel.Slug != page.Slug)
                    {
                        var slugIsAvailable = await _pageService.SlugIsAvailable(request.ViewModel.Slug);
                        if (!slugIsAvailable)
                        {
                            errors.Add(_localizer["The page slug was not changed because the requested slug is already in use."]);
                            request.ModelState.AddModelError("Slug", _localizer["The page slug was not changed because the requested slug is already in use."]);
                        }
                    }
                }
                
                if (request.ModelState.IsValid)
                {
                    var modelString = serializer.Serialize(request.Template.ModelType, model);
                    var renderedModel = await _viewRenderer.RenderViewAsString(request.Template.RenderView, model).ConfigureAwait(false);
                    
                    page.Title = request.ViewModel.Title;
                    page.CorrelationKey = request.ViewModel.CorrelationKey;
                    page.LastModified = DateTime.UtcNow;
                    page.LastModifiedByUser = request.UserName;
                    page.MenuFilters = request.ViewModel.MenuFilters;
                    page.MetaDescription = request.ViewModel.MetaDescription;
                    page.PageOrder = request.ViewModel.PageOrder;

                    page.ShowHeading = request.ViewModel.ShowHeading;
                    page.ShowMenu = request.ViewModel.ShowMenu;
                    page.ShowComments = request.ViewModel.ShowComments;
                    page.MenuFilters = request.ViewModel.MenuFilters;
                    
                    page.ViewRoles = request.ViewModel.ViewRoles;

                    if (!string.IsNullOrEmpty(request.ViewModel.Slug))
                    {
                        page.Slug = request.ViewModel.Slug;
                    }
                    
                    page.ViewRoles = request.ViewModel.ViewRoles;

                    if (!string.IsNullOrEmpty(request.ViewModel.ParentSlug))
                    {
                        var parentPage = await _pageService.GetPageBySlug(request.ViewModel.ParentSlug);
                        if (parentPage != null)
                        {
                            if (parentPage.Id != page.ParentId)
                            {
                                page.ParentId = parentPage.Id;
                                page.ParentSlug = parentPage.Slug;
                            }
                        }
                    }
                    else
                    {
                        // empty means root level
                        page.ParentSlug = string.Empty;
                        page.ParentId = "0";
                    }

                    if (page.ParentSlug == project.DefaultPageSlug)
                    {
                        _log.LogWarning($"{request.UserName} tried to explicitely set the default page slug as the parent slug which is not allowed since all root pages are already children of the default page");
                        page.ParentSlug = string.Empty;
                        page.ParentId = "0";
                    }

                    var shouldFirePublishEvent = false;
                    switch (request.ViewModel.SaveMode)
                    {   
                        case SaveMode.SaveDraft:

                            page.DraftSerializedModel = modelString;
                            page.DraftContent = renderedModel;
                            page.DraftAuthor = request.ViewModel.Author;
                            if (!page.PubDate.HasValue) { page.IsPublished = false; }

                            break;

                        case SaveMode.PublishLater:
                            page.DraftSerializedModel = modelString;
                            page.DraftContent = renderedModel;
                            page.DraftAuthor = request.ViewModel.Author;
                            if (request.ViewModel.NewPubDate.HasValue)
                            {
                                var tzId = await _timeZoneIdResolver.GetUserTimeZoneId(CancellationToken.None);
                                page.DraftPubDate = _timeZoneHelper.ConvertToUtc(request.ViewModel.NewPubDate.Value, tzId);
                            }
                            if (!page.PubDate.HasValue) { page.IsPublished = false; }

                            break;

                        case SaveMode.PublishNow:

                            page.Author = request.ViewModel.Author;
                            page.Content = renderedModel;
                            page.SerializedModel = modelString;

                            page.PubDate = DateTime.UtcNow;
                            page.IsPublished = true;
                            shouldFirePublishEvent = true;

                            page.DraftAuthor = null;
                            page.DraftContent = null;
                            page.DraftPubDate = null;
                            page.DraftSerializedModel = null;

                            break;
                    }

                    await _historyCommands.Create(request.ProjectId, history).ConfigureAwait(false);
                    
                    await _pageService.Update(page);

                    if (shouldFirePublishEvent)
                    {
                        await _pageService.FirePublishEvent(page).ConfigureAwait(false);
                        await _historyCommands.DeleteDraftHistory(project.Id, page.Id).ConfigureAwait(false);
                    }
                    
                    _pageService.ClearNavigationCache();
                    
                }
                
                return new CommandResult<IPage>(page, customModelIsValid && request.ModelState.IsValid, errors);
                
            }
            catch(Exception ex)
            {
                _log.LogError($"{ex.Message}:{ex.StackTrace}");

                errors.Add(_localizer["Updating a page from a content template failed. An error has been logged."]);

                return new CommandResult<IPage>(null, false, errors);
            }

        }
    }
}

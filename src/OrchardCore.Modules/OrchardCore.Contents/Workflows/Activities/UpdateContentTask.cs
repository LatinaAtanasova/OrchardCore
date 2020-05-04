using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Localization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OrchardCore.ContentManagement;
using OrchardCore.Contents.Workflows.Handlers;
using OrchardCore.DisplayManagement.ModelBinding;
using OrchardCore.Workflows.Abstractions.Models;
using OrchardCore.Workflows.Activities;
using OrchardCore.Workflows.Helpers;
using OrchardCore.Workflows.Models;
using OrchardCore.Workflows.Services;

namespace OrchardCore.Contents.Workflows.Activities
{
    public class UpdateContentTask : ContentTask
    {
        private readonly IUpdateModelAccessor _updateModelAccessor;
        private readonly IWorkflowExpressionEvaluator _expressionEvaluator;

        private bool _fromDriver;
        private bool _fromHandler;

        public UpdateContentTask(
            IContentManager contentManager,
            IUpdateModelAccessor updateModelAccessor,
            IWorkflowExpressionEvaluator expressionEvaluator,
            IWorkflowScriptEvaluator scriptEvaluator,
            IStringLocalizer<UpdateContentTask> localizer)
            : base(contentManager, scriptEvaluator, localizer)
        {
            _updateModelAccessor = updateModelAccessor;
            _expressionEvaluator = expressionEvaluator;
        }

        public override string Name => nameof(UpdateContentTask);

        public override LocalizedString Category => S["Content"];

        public override LocalizedString DisplayText => S["Update Content Task"];

        public string ContentType
        {
            get => GetProperty<string>();
            set => SetProperty(value);
        }

        public bool Publish
        {
            get => GetProperty<bool>();
            set => SetProperty(value);
        }

        public bool Inline
        {
            get => GetProperty<bool>();
            set => SetProperty(value);
        }

        public WorkflowExpression<string> ContentItemIdExpression
        {
            get => GetProperty(() => new WorkflowExpression<string>());
            set => SetProperty(value);
        }

        public WorkflowExpression<string> ContentProperties
        {
            get => GetProperty(() => new WorkflowExpression<string>(JsonConvert.SerializeObject(new { DisplayText = S["Enter a title"].Value }, Formatting.Indented)));
            set => SetProperty(value);
        }

        public override bool CanExecute(WorkflowExecutionContext workflowContext, ActivityContext activityContext)
        {
            return !String.IsNullOrEmpty(ContentType);
        }

        public override IEnumerable<Outcome> GetPossibleOutcomes(WorkflowExecutionContext workflowContext, ActivityContext activityContext)
        {
            return Outcomes(S["Done"], S["Failed"]);
        }

        public override Task OnInputReceivedAsync(WorkflowExecutionContext workflowContext, IDictionary<string, object> input)
        {
            // The activity may be executed inline from the 'UserTaskEventContentDriver'.
            if (input.GetValue<string>("UserAction") != null)
            {
                _fromDriver = true;
            }

            // The activity may be executed inline from the 'ContentsHandler'.
            if (input.GetValue<IContent>(ContentsHandler.ContentItemInputKey) != null)
            {
                _fromHandler = true;
            }

            return Task.CompletedTask;
        }

        public async override Task<ActivityExecutionResult> ExecuteAsync(WorkflowExecutionContext workflowContext, ActivityContext activityContext)
        {
            var contentItemId = await GetContentItemIdAsync(workflowContext);

            if (contentItemId == null)
            {
                throw new InvalidOperationException($"The {workflowContext.WorkflowType.Name}:{DisplayText} activity failed to evaluate the 'ContentItemId'.");
            }

            // Check if the activity is executed inline as a content driver or as a content handler.
            var asDriver = _fromDriver && String.Equals(workflowContext.CorrelationId, contentItemId, StringComparison.OrdinalIgnoreCase);
            var asHandler = _fromHandler && String.Equals(workflowContext.CorrelationId, contentItemId, StringComparison.OrdinalIgnoreCase);
            var asDriverOrHandler = asDriver || asHandler;

            ContentItem contentItem = null;

            if (!asHandler)
            {
                // Use 'DraftRequired' so that we mutate a new version unless the type is not 'Versionable'.
                contentItem = await ContentManager.GetAsync(contentItemId, VersionOptions.DraftRequired);
            }
            else
            {
                // If executed as an handler we use the content item that has been passed to the workflow context input.
                contentItem = workflowContext.Input.GetValue<IContent>(ContentsHandler.ContentItemInputKey)?.ContentItem;
            }

            if (contentItem == null)
            {
                throw new InvalidOperationException($"The {workflowContext.WorkflowType.Name}:{DisplayText} activity failed to retrieve the content item.");
            }

            if (!String.IsNullOrWhiteSpace(ContentProperties.Expression))
            {
                var contentProperties = await _expressionEvaluator.EvaluateAsync(ContentProperties, workflowContext);
                contentItem.Merge(JObject.Parse(contentProperties), new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
            }

            // Drivers / handlers are not intended to call content manager methods.
            if (!asDriverOrHandler)
            {
                await ContentManager.UpdateAsync(contentItem);
            }

            workflowContext.CorrelationId = contentItem.ContentItemId;
            workflowContext.Properties[ContentsHandler.ContentItemInputKey] = contentItem;

            // If not acting as a driver / handler, we should call 'ValidateAsync()' that replaces the regular driver validations,
            // idem if acting as an handler as we are executing after the regular validations. And if acting as a driver after an
            // edit 'UserAction', even if we are executing before the part / field drivers, if we added some specific data we may
            // need to call some custom validate handlers. So we always call the content manager 'ValidateAsync()' method.

            var result = await ContentManager.ValidateAsync(contentItem);

            if (result.Succeeded)
            {
                // Drivers / handlers are not intended to call content manager methods.
                if (Publish && !asDriverOrHandler)
                {
                    await ContentManager.PublishAsync(contentItem);
                }

                workflowContext.LastResult = contentItem;
                return Outcomes("Done");
            }
            else
            {
                // Drivers / handlers are intended to add errors to the model state.
                if (asDriverOrHandler)
                {
                    _updateModelAccessor.ModelUpdater.ModelState.AddModelError(nameof(UpdateContentTask),
                        $"The {workflowContext.WorkflowType.Name}:{DisplayText} activity failed to update the content item: "
                        + String.Join(", ", result.Errors));
                }

                workflowContext.LastResult = result;
                return Outcomes("Failed");
            }
        }
    }
}

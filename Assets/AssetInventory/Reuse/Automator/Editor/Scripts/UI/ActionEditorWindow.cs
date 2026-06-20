using ImpossibleRobert.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Automator
{
    /// <summary>
    /// Editor window for creating and editing automation actions.
    /// </summary>
    public class ActionEditorWindow : CommonEditorUI
    {
        private IActionRepository _repository;
        private List<ActionStepDefinition> _steps = new List<ActionStepDefinition>();
        private Vector2 _scrollPos;
        private Action _onSave;
        private Dictionary<string, List<Tuple<string, ParameterValue>>> _parameterOptionsCache = new Dictionary<string, List<Tuple<string, ParameterValue>>>();

        private ReorderableList _stepsListControl;
        private int _selectedStepIndex = -1;

        private ActionDefinition _action;

        public static ActionEditorWindow ShowWindow()
        {
            ActionEditorWindow window = GetWindow<ActionEditorWindow>("Action Editor");
            window.minSize = new Vector2(690, 300);
            return window;
        }

        public static ActionEditorWindow CreateNew(IActionRepository repository, Action onSave = null)
        {
            ActionEditorWindow window = ShowWindow();

            ActionDefinition newAction = new ActionDefinition("New Action");
            newAction = repository.SaveAction(newAction);
            repository.Save();

            window.Init(repository, newAction, onSave);
            return window;
        }

        public static ActionEditorWindow Edit(IActionRepository repository, ActionDefinition action, Action onSave = null)
        {
            ActionEditorWindow window = ShowWindow();
            window.Init(repository, action, onSave);
            return window;
        }

        public void Init(IActionRepository repository, ActionDefinition action, Action onSave = null)
        {
            _repository = repository;
            _action = action;
            _onSave = onSave;

            _steps = _repository.GetSteps(_action.Id);
            _stepsListControl = null;
            _parameterOptionsCache.Clear();
        }

        private ReorderableList StepsListControl
        {
            get
            {
                if (_stepsListControl == null) InitStepsControl();
                return _stepsListControl;
            }
        }

        private void InitStepsControl()
        {
            _stepsListControl = new ReorderableList(_steps, typeof (ActionStepDefinition), true, true, true, true);
            _stepsListControl.drawElementCallback = DrawStepsListItem;
            _stepsListControl.drawHeaderCallback = rect => EditorGUI.LabelField(rect, "Steps to Execute");
            _stepsListControl.onAddCallback = OnAddStep;
            _stepsListControl.onRemoveCallback = OnRemoveStep;
        }

        private void OnAddStep(ReorderableList list)
        {
            GenericMenu menu = new GenericMenu();
            foreach (ActionStep step in ActionStepRegistry.Steps)
            {
                string categoryDisplayName = StringUtils.CamelCaseToWords(step.Category.ToString());
                menu.AddItem(new GUIContent($"{categoryDisplayName}/{step.Name}"), false, () => AddStep(step));
            }
            menu.ShowAsContext();
        }

        private void OnRemoveStep(ReorderableList list)
        {
            if (_selectedStepIndex < 0 || _selectedStepIndex >= _steps.Count) return;
            _steps.RemoveAt(_selectedStepIndex);
            _selectedStepIndex = -1;
        }

        private void DrawStepsListItem(Rect rect, int index, bool isActive, bool isFocused)
        {
            // Draw alternating-row background
            if (Event.current.type == EventType.Repaint && index % 2 == 1)
            {
                Color overlay = EditorGUIUtility.isProSkin
                    ? new Color(1f, 1f, 1f, 0.025f)
                    : new Color(0f, 0f, 0f, 0.025f);
                EditorGUI.DrawRect(rect, overlay);
            }

            if (index >= _steps.Count) return;
            if (isFocused) _selectedStepIndex = index;
            if (!isFocused && _selectedStepIndex == index) _selectedStepIndex = -1;

            ActionStepDefinition stepDef = _steps[index];
            ActionStep step = ActionStepRegistry.GetStep(stepDef.Key);

            if (step == null)
            {
                GUI.Label(new Rect(rect.x, rect.y + 2, rect.width, EditorGUIUtility.singleLineHeight),
                    $"Invalid step definition. Step '{stepDef.Key}' not found.",
                    CommonUIStyles.ColoredText(CommonUIStyles.errorColor));
                return;
            }

            GUI.Label(new Rect(rect.x, rect.y + 2, 150, EditorGUIUtility.singleLineHeight),
                CommonUIStyles.Content(step.Name, step.Description),
                EditorStyles.label);

            int offset = 150;

            // Build dictionary of variables defined in previous steps for validation
            Dictionary<string, string> availableVariables = BuildAvailableVariables(index);

            // Ensure Values list is populated
            while (stepDef.Values.Count < step.Parameters.Count)
            {
                int paramIndex = stepDef.Values.Count;
                stepDef.Values.Add(new ParameterValue(step.Parameters[paramIndex].DefaultValue));
            }

            for (int i = 0; i < step.Parameters.Count; i++)
            {
                StepParameter param = step.Parameters[i];

                // Skip hidden parameters
                if (!step.GetParamVisibility(param, stepDef.Values))
                {
                    continue;
                }

                GUI.Label(new Rect(rect.x + offset, rect.y + 2, 53, EditorGUIUtility.singleLineHeight),
                    CommonUIStyles.Content(param.Name + (param.Optional ? "*" : ""), param.Description),
                    CommonUIStyles.miniLabelRight);

                StepParameter.ParamType finalType = param.Type;
                if (finalType == StepParameter.ParamType.Dynamic)
                {
                    finalType = step.GetParamType(param, stepDef.Values);
                }

                List<Tuple<string, ParameterValue>> finalOptions = param.Options;

                // Handle lazy-loaded options with per-session caching
                if (param.LazyLoadOptions)
                {
                    string cacheKey = $"{step.Key}_{param.Name}";
                    if (!_parameterOptionsCache.TryGetValue(cacheKey, out finalOptions))
                    {
                        finalOptions = step.GetParamOptions(param, stepDef.Values);
                        _parameterOptionsCache[cacheKey] = finalOptions;
                    }
                }
                // Handle dynamic options (recomputed every frame)
                else if (param.Type == StepParameter.ParamType.Dynamic)
                {
                    finalOptions = step.GetParamOptions(param, stepDef.Values);
                }

                if (finalOptions != null && finalOptions.Count > 0)
                {
                    // Render dropdown with values to select from
                    int curIndex = 0;
                    if (finalType == StepParameter.ParamType.String || finalType == StepParameter.ParamType.MultilineString)
                        curIndex = finalOptions.FindIndex(o => o.Item2.stringValue == stepDef.Values[i].stringValue);
                    if (finalType == StepParameter.ParamType.Int)
                        curIndex = finalOptions.FindIndex(o => o.Item2.intValue == stepDef.Values[i].intValue);

                    // Initialize value with first option if current value not found
                    if (curIndex < 0)
                    {
                        curIndex = 0;
                        if (finalType == StepParameter.ParamType.String || finalType == StepParameter.ParamType.MultilineString)
                            stepDef.Values[i].stringValue = finalOptions[0].Item2.stringValue;
                        if (finalType == StepParameter.ParamType.Int)
                            stepDef.Values[i].intValue = finalOptions[0].Item2.intValue;
                    }

                    int newIndex = EditorGUI.Popup(new Rect(rect.x + offset + 55, rect.y + 2, 180, EditorGUIUtility.singleLineHeight),
                        curIndex, finalOptions.Select(o => o.Item1.Replace("/", "\\")).ToArray());

                    if (newIndex != curIndex && newIndex >= 0 && newIndex < finalOptions.Count)
                    {
                        if (finalType == StepParameter.ParamType.String || finalType == StepParameter.ParamType.MultilineString)
                            stepDef.Values[i].stringValue = finalOptions[newIndex].Item2.stringValue;
                        if (finalType == StepParameter.ParamType.Int)
                            stepDef.Values[i].intValue = finalOptions[newIndex].Item2.intValue;
                    }
                }
                else
                {
                    switch (finalType)
                    {
                        case StepParameter.ParamType.String:
                            stepDef.Values[i].stringValue = GUI.TextField(
                                new Rect(rect.x + offset + 55, rect.y + 2, 180, EditorGUIUtility.singleLineHeight),
                                stepDef.Values[i].stringValue ?? "");
                            break;

                        case StepParameter.ParamType.MultilineString:
                            stepDef.Values[i].stringValue = EditorGUI.TextArea(
                                new Rect(rect.x + offset + 55, rect.y + 2, 180, EditorGUIUtility.singleLineHeight),
                                stepDef.Values[i].stringValue ?? "");
                            break;

                        case StepParameter.ParamType.Int:
                            stepDef.Values[i].intValue = EditorGUI.IntField(
                                new Rect(rect.x + offset + 55, rect.y + 2, 180, EditorGUIUtility.singleLineHeight),
                                stepDef.Values[i].intValue);
                            break;

                        case StepParameter.ParamType.Bool:
                            stepDef.Values[i].boolValue = EditorGUI.Toggle(
                                new Rect(rect.x + offset + 55, rect.y + 2, 20, EditorGUIUtility.singleLineHeight),
                                stepDef.Values[i].boolValue);
                            break;
                    }
                }

                // Display warning icon for undefined variables in string parameters
                if (finalType == StepParameter.ParamType.String || finalType == StepParameter.ParamType.MultilineString)
                {
                    string paramValue = stepDef.Values[i].stringValue;
                    if (!string.IsNullOrEmpty(paramValue))
                    {
                        List<string> undefinedVars = VariableResolver.ValidateVariables(paramValue, availableVariables);
                        if (undefinedVars.Count > 0)
                        {
                            Rect iconRect = new Rect(rect.x + offset + 240, rect.y + 2, 20, EditorGUIUtility.singleLineHeight);
                            string tooltip = "Undefined variables: " + string.Join(", ", undefinedVars);
                            GUIContent warningContent = new GUIContent(EditorGUIUtility.IconContent("console.warnicon").image, tooltip);

                            Color originalColor = GUI.color;
                            GUI.color = new Color(1f, 0.7f, 0f, 1f);
                            GUI.Label(iconRect, warningContent);
                            GUI.color = originalColor;
                        }
                    }
                }

                // Run step button
                if (isFocused && GUI.Button(new Rect(rect.x + rect.width - 24, rect.y + 1, 24, 20), EditorGUIUtility.IconContent("d_PlayButton@2x", "|Run Step Now")))
                {
                    try
                    {
                        Dictionary<string, string> variables = BuildAvailableVariables(index);

                        // If this step is a SetTextVariableStep, resolve and add its variable
                        if (step is SetTextVariableStep)
                        {
                            if (SetTextVariableStep.TryExtractVariable(stepDef.Values, out string varName, out string varValue))
                            {
                                varValue = VariableResolver.ReplaceVariables(varValue ?? "", variables);
                                variables[varName] = varValue;
                            }
                        }

                        List<ParameterValue> resolvedValues = ActionRunner.ResolveParameterVariables(
                            stepDef.Values,
                            step.Parameters,
                            step,
                            variables);

                        step.Run(resolvedValues);
                        AssetDatabase.Refresh();
                    }
                    catch (Exception e)
                    {
                        EditorUtility.DisplayDialog("Error Running Step", $"Failed to run step: {e.Message}", "OK");
                    }
                }

                offset += 250;
            }
        }

        public void OnGUI()
        {
            if (_action == null || _repository == null)
            {
                EditorGUILayout.HelpBox("No action loaded. Use CreateNew() or Edit() to initialize.", MessageType.Warning);
                return;
            }

            _action.Name = EditorGUILayout.TextField("Name", _action.Name);
            _action.Description = EditorGUILayout.TextField("Description", _action.Description);
            _action.StopOnFailure = EditorGUILayout.Toggle(CommonUIStyles.Content("Stop on Failure", "If enabled, the action will stop executing remaining steps when a step fails. If disabled, failed steps are logged but execution continues."), _action.StopOnFailure);
            _action.Mode = (ActionDefinition.RunMode)EditorGUILayout.EnumPopup("Run Mode", _action.Mode, GUILayout.Width(300));

            EditorGUILayout.Space(15);
            _scrollPos = GUILayout.BeginScrollView(_scrollPos, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.ExpandWidth(true));
            StepsListControl.DoLayoutList();
            GUILayout.EndScrollView();
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save & Close", CommonUIStyles.mainButton, GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT)))
            {
                Save();
                Close();
            }
            if (GUILayout.Button("Save & Run", GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT)))
            {
                Save();
                RunAction();
            }
            GUILayout.EndHorizontal();
        }

        private async void RunAction()
        {
            ActionRunner runner = new ActionRunner(_repository);
            await runner.RunAction(_action.Id);
            AssetDatabase.Refresh();
        }

        private void Save()
        {
            _repository.SaveAction(_action);

            for (int i = 0; i < _steps.Count; i++)
            {
                ActionStepDefinition step = _steps[i];
                step.OrderIndex = i;
                step.ActionId = _action.Id;
                _repository.SaveStep(step);
            }

            // Delete removed steps
            _repository.DeleteStepsExcept(_action.Id, _steps.Where(s => s.Id > 0).Select(s => s.Id).ToList());

            _repository.Save();

            _onSave?.Invoke();
        }

        private void AddStep(ActionStep step)
        {
            ActionStepDefinition newStep = new ActionStepDefinition
            {
                Key = step.Key,
                ActionId = _action.Id,
                OrderIndex = _steps.Count,
                Values = step.Parameters.Select(p => new ParameterValue(p.DefaultValue)).ToList()
            };

            if (_selectedStepIndex >= 0)
            {
                _steps.Insert(_selectedStepIndex + 1, newStep);
            }
            else
            {
                _steps.Add(newStep);
            }
        }

        private Dictionary<string, string> BuildAvailableVariables(int stepIndex)
        {
            Dictionary<string, string> variables = new Dictionary<string, string>();

            for (int i = 0; i < stepIndex && i < _steps.Count; i++)
            {
                ActionStepDefinition stepDef = _steps[i];
                ActionStep step = ActionStepRegistry.GetStep(stepDef.Key);
                if (step == null) continue;

                if (step is SetTextVariableStep)
                {
                    if (SetTextVariableStep.TryExtractVariable(stepDef.Values, out string varName, out string varValue))
                    {
                        varValue = VariableResolver.ReplaceVariables(varValue ?? "", variables);
                        variables[varName] = varValue;
                    }
                }
            }

            return variables;
        }
    }
}
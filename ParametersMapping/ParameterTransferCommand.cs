using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection; // Добавлено для Reflection
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitLogger;

namespace ParameterTransfer
{
    /// <summary>
    /// Расширения для безопасного получения ID элемента в любой версии Revit.
    /// </summary>
    public static class ElementIdExtensions
    {
        private static PropertyInfo _valueProp;
        private static PropertyInfo _intProp;
        private static bool _initialized = false;
        private static readonly object _lock = new object();

        private static void Initialize(Type idType)
        {
            if (_initialized) return;
            lock (_lock)
            {
                if (_initialized) return;
                // Ищем свойство Value (Revit 2024+, long)
                _valueProp = idType.GetProperty("Value");
                // Ищем свойство IntegerValue (Revit <= 2023, int)
                _intProp = idType.GetProperty("IntegerValue");
                _initialized = true;
            }
        }

        public static long GetIdValue(this ElementId id)
        {
            if (id == null) return -1;

            Initialize(id.GetType());

            if (_valueProp != null)
            {
                var val = _valueProp.GetValue(id);
                if (val is long l) return l;
                if (val is int i) return i;
            }

            if (_intProp != null)
            {
                var val = _intProp.GetValue(id);
                if (val is int i) return i;
            }

            // Fallback на случай непредвиденных изменений API
            return id.GetHashCode();
        }
    }

    /// <summary>
    /// Переносит значения из одного параметра в другой для всех элементов активного вида.
    /// Поддерживает пары Instance/Type в любой комбинации.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ParameterTransferCommand : IExternalCommand
    {
        public static string IS_TAB_NAME => "ISTools";
        public static string IS_NAME => "Перенос параметров";
        public static string IS_IMAGE => "ParameterTransfer.Resources.transfer.png";
        public static string IS_DESCRIPTION => "Перенос значений между параметрами элементов активного вида. Автор: PluginsManager";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            ConfigureLogging(commandData);

            try
            {
                Logger.Info("[ParameterTransfer] Старт команды");

                var uidoc = commandData.Application.ActiveUIDocument;
                var doc = uidoc?.Document;
                if (doc == null)
                {
                    Logger.Warning("[ParameterTransfer] Нет активного документа");
                    message = "Откройте документ Revit.";
                    return Result.Failed;
                }

                var activeView = doc.ActiveView;
                if (activeView == null)
                {
                    Logger.Warning("[ParameterTransfer] Нет активного вида");
                    message = "Нет активного вида.";
                    return Result.Failed;
                }

                Logger.Debug($"[ParameterTransfer] Документ: {doc.Title}, Вид: {activeView.Name}");

                var allParamNames = CollectParameterNames(doc);
                Logger.Debug($"[ParameterTransfer] Собрано имён параметров: {allParamNames.Count}");

                using (var form = new ParameterTransferForm(allParamNames))
                {
                    if (form.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    {
                        Logger.Info("[ParameterTransfer] Пользователь отменил операцию");
                        return Result.Cancelled;
                    }

                    var sourceName = form.SourceParameterName;
                    var targetName = form.TargetParameterName;
                    var overwrite = form.OverwriteExisting;

                    Logger.Info($"[ParameterTransfer] Source='{sourceName}', Target='{targetName}', Overwrite={overwrite}");

                    RunTransfer(doc, activeView, sourceName, targetName, overwrite);
                }

                Logger.Info("[ParameterTransfer] Команда завершена успешно");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Logger.Exception(ex, "[ParameterTransfer] Ошибка выполнения команды");
                message = ex.Message;
                return Result.Failed;
            }
        }

        private void ConfigureLogging(ExternalCommandData commandData)
        {
            Logger.SetLogPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Temp", "ParameterTransfer", "plugin.log"));
            Logger.SetLogLevel(Logger.LogLevel.Debug);
            Logger.Init(
                hostName: "Autodesk Revit",
                hostVersionNumber: commandData.Application.Application.VersionNumber,
                hostBuild: commandData.Application.Application.VersionBuild,
                hasActiveDocument: commandData.Application.ActiveUIDocument != null);
        }

        private List<string> CollectParameterNames(Document doc)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var bindings = doc.ParameterBindings;
            if (bindings != null)
            {
                var iter = bindings.ForwardIterator();
                while (iter.MoveNext())
                {
                    var def = iter.Key;
                    if (def != null && !string.IsNullOrWhiteSpace(def.Name))
                        names.Add(def.Name);
                }
            }

            foreach (Category cat in doc.Settings.Categories)
            {
                try
                {
                    var typeId = cat.Id;
                    var elementType = doc.GetElement(typeId);
                    if (elementType != null)
                    {
                        foreach (Parameter p in elementType.Parameters)
                        {
                            if (p?.Definition != null && !string.IsNullOrWhiteSpace(p.Definition.Name))
                                names.Add(p.Definition.Name);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[ParameterTransfer] Ошибка чтения категории {cat?.Name}: {ex.Message}");
                }
            }

            return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void RunTransfer(Document doc, View activeView, string sourceName, string targetName, bool overwrite)
        {
            var elements = new FilteredElementCollector(doc, activeView.Id)
                .WhereElementIsNotElementType()
                .ToElements();

            Logger.Info($"[ParameterTransfer] Найдено экземпляров на виде: {elements.Count}");
            foreach (var e in elements)
            {
                Logger.Debug($"[ParameterTransfer] Элемент на виде: Id={e.Id.GetIdValue()}, Class={e.GetType().Name}, Name={e.Name}, TypeId={e.GetTypeId().GetIdValue()}");
            }

            if (elements.Count == 0)
            {
                Logger.Warning("[ParameterTransfer] На виде нет элементов");
                return;
            }

            var groups = elements.GroupBy(e => e.GetTypeId()).ToList();
            Logger.Debug($"[ParameterTransfer] Групп по типам: {groups.Count}");

            int copiedCount = 0;
            int skippedCount = 0;
            int warningCount = 0;
            int diffValuesCount = 0;

            using (var tx = new Transaction(doc, "Перенос параметра"))
            {
                tx.Start();

                foreach (var group in groups)
                {
                    var typeId = group.Key;
                    var groupElements = group.ToList();
                    var elementType = typeId != ElementId.InvalidElementId ? doc.GetElement(typeId) : null;

                    Logger.Debug($"[ParameterTransfer] --- Группа типа Id={typeId.GetIdValue()}, Name={elementType?.Name ?? "null"}, Экземпляров={groupElements.Count}");

                    foreach (var elem in groupElements)
                    {
                        var (srcLoc, srcParam) = GetParameterLocation(elem, sourceName);
                        var (tgtLoc, tgtParam) = GetParameterLocation(elem, targetName);

                        Logger.Debug($"[ParameterTransfer] Элемент Id={elem.Id.GetIdValue()}: SrcLoc={srcLoc}, SrcParamId={srcParam?.Id.GetIdValue() ?? -1}, TgtLoc={tgtLoc}, TgtParamId={tgtParam?.Id.GetIdValue() ?? -1}");

                        if (srcLoc == ParamLocation.Instance && tgtLoc == ParamLocation.Instance)
                        {
                            Logger.Debug($"[ParameterTransfer] Сценарий Instance→Instance для элемента {elem.Id.GetIdValue()}");
                            if (CopyParameter(srcParam, tgtParam, overwrite)) copiedCount++;
                            else skippedCount++;
                        }
                        else if (srcLoc == ParamLocation.Type && tgtLoc == ParamLocation.Instance && elementType != null)
                        {
                            var srcTypeParam = elementType.LookupParameter(sourceName);
                            Logger.Debug($"[ParameterTransfer] Сценарий Type→Instance для элемента {elem.Id.GetIdValue()}, SrcTypeParamId={srcTypeParam?.Id.GetIdValue() ?? -1}");
                            if (srcTypeParam != null)
                            {
                                if (CopyParameter(srcTypeParam, tgtParam, overwrite)) copiedCount++;
                                else skippedCount++;
                            }
                            else skippedCount++;
                        }
                    }

                    if (typeId != ElementId.InvalidElementId && elementType != null)
                    {
                        var tgtTypeParam = elementType.LookupParameter(targetName);
                        Logger.Debug($"[ParameterTransfer] Проверка Instance→Type для типа {typeId.GetIdValue()}, TgtTypeParamId={tgtTypeParam?.Id.GetIdValue() ?? -1}");

                        if (tgtTypeParam != null)
                        {
                            var instanceSourceValues = new HashSet<string>(StringComparer.Ordinal);
                            foreach (var elem in groupElements)
                            {
                                var p = elem.LookupParameter(sourceName);
                                if (p != null && p.Element.Id == elem.Id)
                                {
                                    var vs = p.AsValueString() ?? p.AsString() ?? "";
                                    instanceSourceValues.Add(vs);
                                    Logger.Debug($"[ParameterTransfer] Instance→Type: экземпляр {elem.Id.GetIdValue()} имеет значение '{vs}'");
                                }
                                else
                                {
                                    Logger.Debug($"[ParameterTransfer] Instance→Type: у экземпляра {elem.Id.GetIdValue()} нет исходного параметра на уровне экземпляра");
                                }
                            }

                            if (instanceSourceValues.Count > 0)
                            {
                                string newValue;
                                if (instanceSourceValues.Count == 1)
                                {
                                    newValue = instanceSourceValues.First();
                                    Logger.Debug($"[ParameterTransfer] Instance→Type: все значения одинаковы, новое значение='{newValue}'");
                                }
                                else
                                {
                                    newValue = "Разные значения";
                                    diffValuesCount++;
                                    Logger.Warning($"[ParameterTransfer] Тип '{elementType.Name}' (Id={typeId.GetIdValue()}): у экземпляров разные значения, записано '{newValue}'. Уникальные значения: [{string.Join(", ", instanceSourceValues)}]");
                                }

                                if (SetParameterValueFromString(tgtTypeParam, newValue, overwrite))
                                    copiedCount++;
                                else
                                    skippedCount++;
                            }
                            else
                            {
                                Logger.Debug($"[ParameterTransfer] Instance→Type: ни у одного экземпляра типа {typeId.GetIdValue()} нет исходного параметра на уровне экземпляра");
                            }
                        }
                    }

                    if (typeId != ElementId.InvalidElementId && elementType != null)
                    {
                        var srcTypeParam = elementType.LookupParameter(sourceName);
                        var tgtTypeParam = elementType.LookupParameter(targetName);

                        bool srcIsType = srcTypeParam != null && srcTypeParam.Element.Id == elementType.Id;
                        bool tgtIsType = tgtTypeParam != null && tgtTypeParam.Element.Id == elementType.Id;

                        Logger.Debug($"[ParameterTransfer] Проверка Type→Type для типа {typeId.GetIdValue()}: SrcIsType={srcIsType}, TgtIsType={tgtIsType}");

                        if (srcIsType && tgtIsType)
                        {
                            Logger.Debug($"[ParameterTransfer] Сценарий Type→Type для типа {typeId.GetIdValue()}");
                            if (CopyParameter(srcTypeParam, tgtTypeParam, overwrite))
                                copiedCount++;
                            else
                                skippedCount++;
                        }
                    }
                }

                tx.Commit();
                Logger.Debug("[ParameterTransfer] --- Проверка значений после коммита транзакции ---");
                foreach (var group in groups)
                {
                    var typeId = group.Key;
                    if (typeId == ElementId.InvalidElementId) continue;
                    var elementType = doc.GetElement(typeId);
                    if (elementType == null) continue;

                    var tgtTypeParam = elementType.LookupParameter(targetName);
                    if (tgtTypeParam != null && tgtTypeParam.Element.Id == elementType.Id)
                    {
                        var val = tgtTypeParam.AsValueString() ?? tgtTypeParam.AsString() ?? "";
                        Logger.Debug($"[ParameterTransfer] После коммита: Тип Id={typeId.GetIdValue()}, Name={elementType.Name}, Параметр='{targetName}', Значение='{val}'");
                    }
                }
            }

            Logger.Info($"[ParameterTransfer] Итог: скопировано={copiedCount}, пропущено={skippedCount}, предупреждений={warningCount}, 'Разные значения'={diffValuesCount}");
            DebugWindow.AddRow($"Скопировано: {copiedCount}");
            DebugWindow.AddRow($"Пропущено: {skippedCount}");
            DebugWindow.AddRow($"Разные значения: {diffValuesCount}");
            DebugWindow.Show("Итог переноса");
        }

        private enum ParamLocation { Instance, Type, NotFound }

        private (ParamLocation location, Parameter param) GetParameterLocation(Element element, string paramName)
        {
            var p = element.LookupParameter(paramName);
            if (p != null)
            {
                bool isInstance = p.Element.Id == element.Id;
                Logger.Debug($"[ParameterTransfer] LookupParameter('{paramName}') для элемента {element.Id.GetIdValue()}: найден ParamId={p.Id.GetIdValue()}, OwnerId={p.Element.Id.GetIdValue()}, IsInstance={isInstance}");
                if (isInstance)
                    return (ParamLocation.Instance, p);
            }

            var typeId = element.GetTypeId();
            if (typeId != ElementId.InvalidElementId)
            {
                var type = element.Document.GetElement(typeId);
                if (type != null)
                {
                    var tp = type.LookupParameter(paramName);
                    if (tp != null && tp.Element.Id == type.Id)
                    {
                        Logger.Debug($"[ParameterTransfer] LookupParameter('{paramName}') для типа {typeId.GetIdValue()}: найден ParamId={tp.Id.GetIdValue()}, OwnerId={tp.Element.Id.GetIdValue()}");
                        return (ParamLocation.Type, tp);
                    }
                }
            }

            Logger.Debug($"[ParameterTransfer] Параметр '{paramName}' не найден ни у элемента {element.Id.GetIdValue()}, ни у его типа");
            return (ParamLocation.NotFound, null);
        }

        private bool CopyParameter(Parameter src, Parameter target, bool overwrite)
        {
            if (src == null || target == null) return false;
            if (target.IsReadOnly)
            {
                Logger.Warning($"[ParameterTransfer] Целевой параметр '{target.Definition.Name}' только для чтения");
                return false;
            }

            var valueString = src.AsValueString() ?? src.AsString();
            if (string.IsNullOrEmpty(valueString))
            {
                Logger.Debug($"[ParameterTransfer] Исходное значение пусто, элемент {src.Element.Id.GetIdValue()}, пропуск");
                return false;
            }

            if (!overwrite)
            {
                var existingValue = target.AsValueString() ?? target.AsString();
                if (!string.IsNullOrEmpty(existingValue))
                {
                    Logger.Debug($"[ParameterTransfer] Значение уже задано ('{existingValue}'), перезапись отключена, пропуск");
                    return false;
                }
            }

            return SetParameterValueFromString(target, valueString, overwrite);
        }

        private bool SetParameterValueFromString(Parameter target, string valueString, bool overwrite)
        {
            if (target == null || target.IsReadOnly)
            {
                Logger.Warning($"[ParameterTransfer] Пропуск записи: параметр null или ReadOnly. ParamId={target?.Id.GetIdValue() ?? -1}");
                return false;
            }

            var beforeValue = target.AsValueString() ?? target.AsString() ?? "";
            Logger.Debug($"[ParameterTransfer] До записи: ParamId={target.Id.GetIdValue()}, OwnerId={target.Element.Id.GetIdValue()}, StorageType={target.StorageType}, CurrentValue='{beforeValue}', NewValue='{valueString}'");

            try
            {
                bool success = false;

                if (target.StorageType == StorageType.String)
                {
                    target.Set(valueString);
                    success = true;
                    Logger.Debug($"[ParameterTransfer] Использован Set(string) для StorageType.String");
                }
                else
                {
                    success = target.SetValueString(valueString);
                    Logger.Debug($"[ParameterTransfer] Использован SetValueString для StorageType.{target.StorageType}, returned={success}");
                }

                var afterValue = target.AsValueString() ?? target.AsString() ?? "";
                Logger.Debug($"[ParameterTransfer] После записи: AfterValue='{afterValue}'");

                if (!success || afterValue != valueString)
                {
                    Logger.Warning($"[ParameterTransfer] Запись не удалась: ParamId={target.Id.GetIdValue()}, Expected='{valueString}', Got='{afterValue}', Success={success}");
                    return false;
                }

                Logger.Debug($"[ParameterTransfer] Успешно записано '{valueString}' в параметр '{target.Definition.Name}' элемента {target.Element.Id.GetIdValue()}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Exception(ex, $"[ParameterTransfer] Исключение при записи '{valueString}' в ParamId={target.Id.GetIdValue()}");
                return false;
            }
        }
    }
}
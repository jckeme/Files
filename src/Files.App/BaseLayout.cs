using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI;
using CommunityToolkit.WinUI.UI;
using Files.App.DataModels;
using Files.App.EventArguments;
using Files.App.Extensions;
using Files.App.Filesystem;
using Files.App.Filesystem.StorageItems;
using Files.App.Helpers;
using Files.App.Helpers.ContextFlyouts;
using Files.App.Interacts;
using Files.App.UserControls;
using Files.App.UserControls.Menus;
using Files.App.ViewModels;
using Files.App.Views;
using Files.Backend.Services.Settings;
using Files.Shared.Enums;
using Files.Shared.Extensions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;
using Vanara.PInvoke;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.System;
using static Files.App.Helpers.PathNormalization;
using VA = Vanara.Windows.Shell;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Files.App
{
	/// <summary>
	/// The base class which every layout page must derive from
	/// </summary>
	public abstract class BaseLayout : Page, IBaseLayout, INotifyPropertyChanged
	{
		private readonly DispatcherQueueTimer jumpTimer;

		protected IUserSettingsService UserSettingsService { get; } = Ioc.Default.GetService<IUserSettingsService>()!;

		protected IFileTagsSettingsService FileTagsSettingsService { get; } = Ioc.Default.GetService<IFileTagsSettingsService>()!;

		public SelectedItemsPropertiesViewModel SelectedItemsPropertiesViewModel { get; }

		public FolderSettingsViewModel? FolderSettings => ParentShellPageInstance?.InstanceViewModel.FolderSettings;

		public CurrentInstanceViewModel? InstanceViewModel => ParentShellPageInstance?.InstanceViewModel;

		public IPaneViewModel PaneViewModel => App.PaneViewModel;

		public AppModel AppModel => App.AppModel;
		public DirectoryPropertiesViewModel DirectoryPropertiesViewModel { get; }

		public CommandBarFlyout ItemContextMenuFlyout { get; set; } = new CommandBarFlyout()
		{
			AlwaysExpanded = true,
		};
		public CommandBarFlyout BaseContextMenuFlyout { get; set; } = new CommandBarFlyout()
		{
			AlwaysExpanded = true,
		};

		public BaseLayoutCommandsViewModel? CommandsViewModel { get; protected set; }

		public IShellPage? ParentShellPageInstance { get; private set; } = null;

		public bool IsRenamingItem { get; set; } = false;
		public ListedItem? RenamingItem { get; set; } = null;

		public string? OldItemName { get; set; } = null;

		private bool isMiddleClickToScrollEnabled = true;

		public bool IsMiddleClickToScrollEnabled
		{
			get => isMiddleClickToScrollEnabled;
			set
			{
				if (isMiddleClickToScrollEnabled != value)
				{
					isMiddleClickToScrollEnabled = value;
					NotifyPropertyChanged(nameof(IsMiddleClickToScrollEnabled));
				}
			}
		}

		protected AddressToolbar? NavToolbar => (App.Window.Content as Frame)?.FindDescendant<AddressToolbar>();

		private CollectionViewSource collectionViewSource = new CollectionViewSource()
		{
			IsSourceGrouped = true,
		};

		public CollectionViewSource CollectionViewSource
		{
			get => collectionViewSource;
			set
			{
				if (collectionViewSource == value)
					return;
				if (collectionViewSource.View is not null)
					collectionViewSource.View.VectorChanged -= View_VectorChanged;
				collectionViewSource = value;
				NotifyPropertyChanged(nameof(CollectionViewSource));
				if (collectionViewSource.View is not null)
					collectionViewSource.View.VectorChanged += View_VectorChanged;
			}
		}

		protected NavigationArguments? navigationArguments;

		private bool isItemSelected = false;

		public bool IsItemSelected
		{
			get => isItemSelected;
			internal set
			{
				if (value != isItemSelected)
				{
					isItemSelected = value;
					NotifyPropertyChanged(nameof(IsItemSelected));
				}
			}
		}

		private string jumpString = string.Empty;

		public string JumpString
		{
			get => jumpString;
			set
			{
				// If current string is "a", and the next character typed is "a",
				// search for next file that starts with "a" (a.k.a. _jumpString = "a")
				if (jumpString.Length == 1 && value == jumpString + jumpString)
					value = jumpString;
				if (value != string.Empty)
				{
					ListedItem? jumpedToItem = null;
					ListedItem? previouslySelectedItem = null;

					if (IsItemSelected)
						previouslySelectedItem = SelectedItem;

					// Select first matching item after currently selected item
					if (previouslySelectedItem is not null)
					{
						// Use FilesAndFolders because only displayed entries should be jumped to
						IEnumerable<ListedItem> candidateItems = ParentShellPageInstance!.FilesystemViewModel.FilesAndFolders
							.SkipWhile(x => x != previouslySelectedItem)
							.Skip(value.Length == 1 ? 1 : 0) // User is trying to cycle through items starting with the same letter
							.Where(f => f.Name.Length >= value.Length && string.Equals(f.Name.Substring(0, value.Length), value, StringComparison.OrdinalIgnoreCase));
						jumpedToItem = candidateItems.FirstOrDefault();
					}

					if (jumpedToItem is null)
					{
						// Use FilesAndFolders because only displayed entries should be jumped to
						IEnumerable<ListedItem> candidateItems = ParentShellPageInstance!.FilesystemViewModel.FilesAndFolders
							.Where(f => f.Name.Length >= value.Length && string.Equals(f.Name.Substring(0, value.Length), value, StringComparison.OrdinalIgnoreCase));
						jumpedToItem = candidateItems.FirstOrDefault();
					}

					if (jumpedToItem is not null)
					{
						ItemManipulationModel.SetSelectedItem(jumpedToItem);
						ItemManipulationModel.ScrollIntoView(jumpedToItem);
						ItemManipulationModel.FocusSelectedItems();
					}

					// Restart the timer
					jumpTimer.Start();
				}
				jumpString = value;
			}
		}

		private List<ListedItem>? selectedItems = new List<ListedItem>();

		public List<ListedItem>? SelectedItems
		{
			get => selectedItems;
			internal set
			{
				//if (!(value?.All(x => selectedItems?.Contains(x) ?? false) ?? value == selectedItems)) // check if the new list is different then the old one
				if (value != selectedItems) // check if the new list is different then the old one
				{
					if (value?.FirstOrDefault() != selectedItems?.FirstOrDefault())
					{
						// update preview pane properties
						App.PreviewPaneViewModel.IsItemSelected = value?.Count > 0;
						App.PreviewPaneViewModel.SelectedItem = value?.Count == 1 ? value.First() : null;

						// check if the preview pane is open before updating the model
						if (PaneViewModel.IsPreviewSelected)
						{
							bool isPaneEnabled = ((App.Window.Content as Frame)?.Content as MainPage)?.IsPaneEnabled ?? false;
							if (isPaneEnabled)
								App.PreviewPaneViewModel.UpdateSelectedItemPreview();
						}
					}

					selectedItems = value;
					if (selectedItems?.Count == 0 || selectedItems?[0] is null)
					{
						IsItemSelected = false;
						SelectedItem = null;
						SelectedItemsPropertiesViewModel.IsItemSelected = false;
						ResetRenameDoubleClick();
						UpdateSelectionSize();
					}
					else if (selectedItems != null)
					{
						IsItemSelected = true;
						SelectedItem = selectedItems.First();
						SelectedItemsPropertiesViewModel.IsItemSelected = true;
						UpdateSelectionSize();

						SelectedItemsPropertiesViewModel.SelectedItemsCount = selectedItems.Count;

						if (selectedItems.Count == 1)
						{
							SelectedItemsPropertiesViewModel.SelectedItemsCountString = $"{selectedItems.Count} {"ItemSelected/Text".GetLocalizedResource()}";
							DispatcherQueue.EnqueueAsync(async () =>
							{
								await Task.Delay(50); // Tapped event must be executed first
								preRenamingItem = SelectedItem;
							});
						}
						else
						{
							SelectedItemsPropertiesViewModel.SelectedItemsCountString = $"{selectedItems!.Count} {"ItemsSelected/Text".GetLocalizedResource()}";
							ResetRenameDoubleClick();
						}
					}

					NotifyPropertyChanged(nameof(SelectedItems));
					//ItemManipulationModel.SetDragModeForItems();
				}

				ParentShellPageInstance!.ToolbarViewModel.SelectedItems = value;
			}
		}

		public ListedItem? SelectedItem { get; private set; }

		private readonly DispatcherQueueTimer dragOverTimer, tapDebounceTimer, hoverTimer;

		protected abstract uint IconSize { get; }

		protected abstract ItemsControl ItemsControl { get; }

		public BaseLayout()
		{
			ItemManipulationModel = new ItemManipulationModel();

			HookBaseEvents();
			HookEvents();

			jumpTimer = DispatcherQueue.CreateTimer();
			jumpTimer.Interval = TimeSpan.FromSeconds(0.8);
			jumpTimer.Tick += JumpTimer_Tick;

			SelectedItemsPropertiesViewModel = new SelectedItemsPropertiesViewModel();
			DirectoryPropertiesViewModel = new DirectoryPropertiesViewModel();

			dragOverTimer = DispatcherQueue.CreateTimer();
			tapDebounceTimer = DispatcherQueue.CreateTimer();
			hoverTimer = DispatcherQueue.CreateTimer();
		}

		protected abstract void HookEvents();

		protected abstract void UnhookEvents();

		private void HookBaseEvents()
		{
			ItemManipulationModel.RefreshItemsOpacityInvoked += ItemManipulationModel_RefreshItemsOpacityInvoked;
		}

		private void UnhookBaseEvents()
		{
			ItemManipulationModel.RefreshItemsOpacityInvoked -= ItemManipulationModel_RefreshItemsOpacityInvoked;
		}

		public ItemManipulationModel ItemManipulationModel { get; private set; }

		private void JumpTimer_Tick(object sender, object e)
		{
			jumpString = string.Empty;
			jumpTimer.Stop();
		}

		protected abstract void InitializeCommandsViewModel();

		protected IEnumerable<ListedItem>? GetAllItems()
		{
			var items = CollectionViewSource.IsSourceGrouped ? // add all items from each group to the new list
				(CollectionViewSource.Source as BulkConcurrentObservableCollection<GroupedCollection<ListedItem>>)?.SelectMany(g => g) :
				CollectionViewSource.Source as IEnumerable<ListedItem>;
			return items ?? new List<ListedItem>();
		}

		public virtual void ResetItemOpacity()
		{
			foreach (var item in GetAllItems())
			{
				if (item is not null)
					item.Opacity = item.IsHiddenItem ? Constants.UI.DimItemOpacity : 1.0d;
			}
		}

		protected ListedItem? GetItemFromElement(object element)
		{
			var item = element as ContentControl;
			if (item is null || !CanGetItemFromElement(element))
				return null;

			return (item.DataContext as ListedItem) ?? (item.Content as ListedItem) ?? (ItemsControl.ItemFromContainer(item) as ListedItem);
		}

		protected abstract bool CanGetItemFromElement(object element);

		protected virtual void BaseFolderSettings_LayoutModeChangeRequested(object? sender, LayoutModeEventArgs e)
		{
			if (ParentShellPageInstance?.SlimContentPage is not null)
			{
				var layoutType = FolderSettings!.GetLayoutType(ParentShellPageInstance.FilesystemViewModel.WorkingDirectory);

				if (layoutType != ParentShellPageInstance.CurrentPageType)
				{
					ParentShellPageInstance.NavigateWithArguments(layoutType, new NavigationArguments()
					{
						NavPathParam = navigationArguments!.NavPathParam,
						IsSearchResultPage = navigationArguments.IsSearchResultPage,
						SearchPathParam = navigationArguments.SearchPathParam,
						SearchQuery = navigationArguments.SearchQuery,
						SearchUnindexedItems = navigationArguments.SearchUnindexedItems,
						IsLayoutSwitch = true,
						AssociatedTabInstance = ParentShellPageInstance
					});

					// Remove old layout from back stack
					ParentShellPageInstance.RemoveLastPageFromBackStack();
				}
				ParentShellPageInstance.FilesystemViewModel.UpdateEmptyTextType();
			}
		}

		public event PropertyChangedEventHandler? PropertyChanged;

		protected void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		}

		protected override async void OnNavigatedTo(NavigationEventArgs eventArgs)
		{
			base.OnNavigatedTo(eventArgs);
			// Add item jumping handler
			this.CharacterReceived += Page_CharacterReceived;
			navigationArguments = (NavigationArguments)eventArgs.Parameter;
			ParentShellPageInstance = navigationArguments.AssociatedTabInstance;
			InitializeCommandsViewModel();

			IsItemSelected = false;
			FolderSettings!.LayoutModeChangeRequested += BaseFolderSettings_LayoutModeChangeRequested;
			FolderSettings.GroupOptionPreferenceUpdated += FolderSettings_GroupOptionPreferenceUpdated;
			ParentShellPageInstance.FilesystemViewModel.EmptyTextType = EmptyTextType.None;
			ParentShellPageInstance.ToolbarViewModel.UpdateSortAndGroupOptions();
			ParentShellPageInstance.ToolbarViewModel.CanRefresh = true;

			if (!navigationArguments.IsSearchResultPage)
			{
				string previousDir = ParentShellPageInstance.FilesystemViewModel.WorkingDirectory;
				await ParentShellPageInstance.FilesystemViewModel.SetWorkingDirectoryAsync(navigationArguments.NavPathParam);

				// pathRoot will be empty on recycle bin path
				var workingDir = ParentShellPageInstance.FilesystemViewModel.WorkingDirectory ?? string.Empty;
				string pathRoot = GetPathRoot(workingDir);

				bool isRecycleBin = workingDir.StartsWith(CommonPaths.RecycleBinPath, StringComparison.Ordinal);
				ParentShellPageInstance.InstanceViewModel.IsPageTypeRecycleBin = isRecycleBin;

				// Can't go up from recycle bin
				ParentShellPageInstance.ToolbarViewModel.CanNavigateToParent = !(string.IsNullOrEmpty(pathRoot) || isRecycleBin);

				ParentShellPageInstance.InstanceViewModel.IsPageTypeMtpDevice = workingDir.StartsWith("\\\\?\\", StringComparison.Ordinal);
				ParentShellPageInstance.InstanceViewModel.IsPageTypeFtp = FtpHelpers.IsFtpPath(workingDir);
				ParentShellPageInstance.InstanceViewModel.IsPageTypeZipFolder = ZipStorageFolder.IsZipPath(workingDir);
				ParentShellPageInstance.InstanceViewModel.IsPageTypeLibrary = LibraryManager.IsLibraryPath(workingDir);
				ParentShellPageInstance.InstanceViewModel.IsPageTypeSearchResults = false;
				ParentShellPageInstance.ToolbarViewModel.PathControlDisplayText = navigationArguments.NavPathParam;
				if (!navigationArguments.IsLayoutSwitch || previousDir != workingDir)
					ParentShellPageInstance.FilesystemViewModel.RefreshItems(previousDir, SetSelectedItemsOnNavigation);
				else
					ParentShellPageInstance.ToolbarViewModel.CanGoForward = false;
			}
			else
			{
				await ParentShellPageInstance.FilesystemViewModel.SetWorkingDirectoryAsync(navigationArguments.SearchPathParam);

				ParentShellPageInstance.ToolbarViewModel.CanGoForward = false;
				ParentShellPageInstance.ToolbarViewModel.CanGoBack = true;  // Impose no artificial restrictions on back navigation. Even in a search results page.
				ParentShellPageInstance.ToolbarViewModel.CanNavigateToParent = false;

				var workingDir = ParentShellPageInstance.FilesystemViewModel.WorkingDirectory ?? string.Empty;
				ParentShellPageInstance.InstanceViewModel.IsPageTypeRecycleBin = workingDir.StartsWith(CommonPaths.RecycleBinPath, StringComparison.Ordinal);
				ParentShellPageInstance.InstanceViewModel.IsPageTypeMtpDevice = workingDir.StartsWith("\\\\?\\", StringComparison.Ordinal);
				ParentShellPageInstance.InstanceViewModel.IsPageTypeFtp = FtpHelpers.IsFtpPath(workingDir);
				ParentShellPageInstance.InstanceViewModel.IsPageTypeZipFolder = ZipStorageFolder.IsZipPath(workingDir);
				ParentShellPageInstance.InstanceViewModel.IsPageTypeLibrary = LibraryManager.IsLibraryPath(workingDir);
				ParentShellPageInstance.InstanceViewModel.IsPageTypeSearchResults = true;

				if (!navigationArguments.IsLayoutSwitch)
				{
					var displayName = App.LibraryManager.TryGetLibrary(navigationArguments.SearchPathParam, out var lib) ? lib.Text : navigationArguments.SearchPathParam;
					ParentShellPageInstance.UpdatePathUIToWorkingDirectory(null, string.Format("SearchPagePathBoxOverrideText".GetLocalizedResource(), navigationArguments.SearchQuery, displayName));
					var searchInstance = new Filesystem.Search.FolderSearch
					{
						Query = navigationArguments.SearchQuery,
						Folder = navigationArguments.SearchPathParam,
						ThumbnailSize = InstanceViewModel!.FolderSettings.GetIconSize(),
						SearchUnindexedItems = navigationArguments.SearchUnindexedItems
					};
					_ = ParentShellPageInstance.FilesystemViewModel.SearchAsync(searchInstance);
				}
			}

			ParentShellPageInstance.InstanceViewModel.IsPageTypeNotHome = true; // show controls that were hidden on the home page
			ParentShellPageInstance.FilesystemViewModel.UpdateGroupOptions();
			UpdateCollectionViewSource();
			FolderSettings.IsLayoutModeChanging = false;

			SetSelectedItemsOnNavigation();

			ItemContextMenuFlyout.Opening += ItemContextFlyout_Opening;
			BaseContextMenuFlyout.Opening += BaseContextFlyout_Opening;
		}

		public void SetSelectedItemsOnNavigation()
		{
			try
			{
				if (navigationArguments is not null && navigationArguments.SelectItems is not null && navigationArguments.SelectItems.Any())
				{
					List<ListedItem> liItemsToSelect = new List<ListedItem>();
					foreach (string item in navigationArguments.SelectItems)
						liItemsToSelect.Add(ParentShellPageInstance!.FilesystemViewModel.FilesAndFolders.Where((li) => li.ItemNameRaw == item).First());

					ItemManipulationModel.SetSelectedItems(liItemsToSelect);
					ItemManipulationModel.FocusSelectedItems();
				}
				else if (navigationArguments is not null && navigationArguments.FocusOnNavigation)
				{
					ItemManipulationModel.FocusFileList(); // Set focus on layout specific file list control
				}
			}
			catch (Exception)
			{
			}
		}

		private CancellationTokenSource? groupingCancellationToken;

		private async void FolderSettings_GroupOptionPreferenceUpdated(object? sender, GroupOption e)
		{
			// Two or more of these running at the same time will cause a crash, so cancel the previous one before beginning
			groupingCancellationToken?.Cancel();
			groupingCancellationToken = new CancellationTokenSource();
			var token = groupingCancellationToken.Token;
			await ParentShellPageInstance!.FilesystemViewModel.GroupOptionsUpdated(token);
			UpdateCollectionViewSource();
			await ParentShellPageInstance.FilesystemViewModel.ReloadItemGroupHeaderImagesAsync();
		}

		protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
		{
			base.OnNavigatingFrom(e);
			// Remove item jumping handler
			this.CharacterReceived -= Page_CharacterReceived;
			FolderSettings!.LayoutModeChangeRequested -= BaseFolderSettings_LayoutModeChangeRequested;
			FolderSettings.GroupOptionPreferenceUpdated -= FolderSettings_GroupOptionPreferenceUpdated;
			ItemContextMenuFlyout.Opening -= ItemContextFlyout_Opening;
			BaseContextMenuFlyout.Opening -= BaseContextFlyout_Opening;

			var parameter = e.Parameter as NavigationArguments;
			if (parameter is not null && !parameter.IsLayoutSwitch)
				ParentShellPageInstance!.FilesystemViewModel.CancelLoadAndClearFiles();
		}

		public async void ItemContextFlyout_Opening(object? sender, object e)
		{
			try
			{
				if (!IsItemSelected && ((sender as CommandBarFlyout)?.Target as ListViewItem)?.Content is ListedItem li) // Workaround for item sometimes not getting selected
				{
					ItemManipulationModel.SetSelectedItem(li);
				}
				if (IsItemSelected)
					await LoadMenuItemsAsync();
			}
			catch (Exception error)
			{
				Debug.WriteLine(error);
			}
		}

		private CancellationTokenSource? shellContextMenuItemCancellationToken;

		public async void BaseContextFlyout_Opening(object? sender, object e)
		{
			try
			{
				if (BaseContextMenuFlyout.GetValue(ContextMenuExtensions.ItemsControlProperty) is ItemsControl itc)
					itc.MaxHeight = Constants.UI.ContextMenuMaxHeight; // Reset menu max height
				shellContextMenuItemCancellationToken?.Cancel();
				shellContextMenuItemCancellationToken = new CancellationTokenSource();
				var shiftPressed = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
				var items = ContextFlyoutItemHelper.GetBaseContextCommandsWithoutShellItems(currentInstanceViewModel: InstanceViewModel!, itemViewModel: ParentShellPageInstance!.FilesystemViewModel, commandsViewModel: CommandsViewModel!, shiftPressed: shiftPressed, false);
				BaseContextMenuFlyout.PrimaryCommands.Clear();
				BaseContextMenuFlyout.SecondaryCommands.Clear();
				var (primaryElements, secondaryElements) = ItemModelListToContextFlyoutHelper.GetAppBarItemsFromModel(items);
				AddCloseHandler(primaryElements, secondaryElements);
				primaryElements.ForEach(i => BaseContextMenuFlyout.PrimaryCommands.Add(i));
				secondaryElements.OfType<FrameworkElement>().ForEach(i => i.MinWidth = Constants.UI.ContextMenuItemsMaxWidth); // Set menu min width
				secondaryElements.ForEach(i => BaseContextMenuFlyout.SecondaryCommands.Add(i));

				if (!InstanceViewModel!.IsPageTypeSearchResults && !InstanceViewModel.IsPageTypeZipFolder)
				{
					var shellMenuItems = await ContextFlyoutItemHelper.GetBaseContextShellCommandsAsync(currentInstanceViewModel: InstanceViewModel, workingDir: ParentShellPageInstance.FilesystemViewModel.WorkingDirectory, shiftPressed: shiftPressed, showOpenMenu: false, shellContextMenuItemCancellationToken.Token);
					if (shellMenuItems.Any())
						AddShellItemsToMenu(shellMenuItems, BaseContextMenuFlyout, shiftPressed);
				}
			}
			catch (Exception error)
			{
				Debug.WriteLine(error);
			}
		}

		public void UpdateSelectionSize()
		{
			var items = (selectedItems?.Any() ?? false) ? selectedItems : GetAllItems();
			if (items is null)
				return;
			bool isSizeKnown = !items.Any(item => string.IsNullOrEmpty(item.FileSize));
			if (isSizeKnown)
			{
				long size = items.Sum(item => item.FileSizeBytes);
				SelectedItemsPropertiesViewModel.ItemSizeBytes = size;
				SelectedItemsPropertiesViewModel.ItemSize = size.ToSizeString();
			}
			else
			{
				SelectedItemsPropertiesViewModel.ItemSizeBytes = 0;
				SelectedItemsPropertiesViewModel.ItemSize = string.Empty;
			}
			SelectedItemsPropertiesViewModel.ItemSizeVisibility = isSizeKnown;
		}

		private async Task LoadMenuItemsAsync()
		{
			if (ItemContextMenuFlyout.GetValue(ContextMenuExtensions.ItemsControlProperty) is ItemsControl itc)
				itc.MaxHeight = Constants.UI.ContextMenuMaxHeight; // Reset menu max height
			shellContextMenuItemCancellationToken?.Cancel();
			shellContextMenuItemCancellationToken = new CancellationTokenSource();
			SelectedItemsPropertiesViewModel.CheckAllFileExtensions(this.SelectedItems!.Select(selectedItem => selectedItem?.FileExtension).ToList()!);
			var shiftPressed = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
			var items = ContextFlyoutItemHelper.GetItemContextCommandsWithoutShellItems(currentInstanceViewModel: InstanceViewModel!, workingDir: ParentShellPageInstance!.FilesystemViewModel.WorkingDirectory, selectedItems: SelectedItems!, selectedItemsPropertiesViewModel: SelectedItemsPropertiesViewModel, commandsViewModel: CommandsViewModel!, shiftPressed: shiftPressed, showOpenMenu: false);
			ItemContextMenuFlyout.PrimaryCommands.Clear();
			ItemContextMenuFlyout.SecondaryCommands.Clear();
			var (primaryElements, secondaryElements) = ItemModelListToContextFlyoutHelper.GetAppBarItemsFromModel(items);
			AddCloseHandler(primaryElements, secondaryElements);
			primaryElements.ForEach(i => ItemContextMenuFlyout.PrimaryCommands.Add(i));
			secondaryElements.OfType<FrameworkElement>().ForEach(i => i.MinWidth = Constants.UI.ContextMenuItemsMaxWidth); // Set menu min width
			secondaryElements.ForEach(i => ItemContextMenuFlyout.SecondaryCommands.Add(i));

			if (InstanceViewModel!.CanTagFilesInPage)
				AddNewFileTagsToMenu(ItemContextMenuFlyout);

			if (!InstanceViewModel.IsPageTypeZipFolder)
			{
				var shellMenuItems = await ContextFlyoutItemHelper.GetItemContextShellCommandsAsync(currentInstanceViewModel: InstanceViewModel, workingDir: ParentShellPageInstance.FilesystemViewModel.WorkingDirectory, selectedItems: SelectedItems!, shiftPressed: shiftPressed, showOpenMenu: false, shellContextMenuItemCancellationToken.Token);
				if (shellMenuItems.Any())
					AddShellItemsToMenu(shellMenuItems, ItemContextMenuFlyout, shiftPressed);
			}
		}

		private void AddCloseHandler(IList<ICommandBarElement> primaryElements, IList<ICommandBarElement> secondaryElements)
		{
			// Workaround for WinUI (#5508)
			var closeHandler = new RoutedEventHandler((s, e) => ItemContextMenuFlyout.Hide());

			primaryElements
				.OfType<AppBarButton>()
				.ForEach(button => button.Click += closeHandler);

			secondaryElements
				.OfType<AppBarButton>()
				.Select(item => item.Flyout)
				.OfType<MenuFlyout>()
				.SelectMany(menu => menu.Items)
				.OfType<MenuFlyoutItem>()
				.ForEach(button => button.Click += closeHandler);
		}

		private void AddNewFileTagsToMenu(CommandBarFlyout contextMenu)
		{
			var fileTagsContextMenu = new FileTagsContextMenu(SelectedItems!);
			var overflowSeparator = contextMenu.SecondaryCommands.FirstOrDefault(x => x is FrameworkElement fe && fe.Tag as string == "OverflowSeparator") as AppBarSeparator;
			var index = contextMenu.SecondaryCommands.IndexOf(overflowSeparator);
			index = index >= 0 ? index : contextMenu.SecondaryCommands.Count;
			contextMenu.SecondaryCommands.Insert(index, new AppBarSeparator());
			contextMenu.SecondaryCommands.Insert(index + 1, new AppBarButton()
			{
				Label = "SettingsEditFileTagsExpander/Title".GetLocalizedResource(),
				Icon = new FontIcon() { Glyph = "\uE1CB" },
				Flyout = fileTagsContextMenu
			});
		}

		private void AddShellItemsToMenu(List<ContextMenuFlyoutItemViewModel> shellMenuItems, CommandBarFlyout contextMenuFlyout, bool shiftPressed)
		{
			var openWithSubItems = ItemModelListToContextFlyoutHelper.GetMenuFlyoutItemsFromModel(ShellContextmenuHelper.GetOpenWithItems(shellMenuItems));
			var mainShellMenuItems = shellMenuItems.RemoveFrom(!UserSettingsService.AppearanceSettingsService.MoveOverflowMenuItemsToSubMenu ? int.MaxValue : shiftPressed ? 6 : 4);
			var overflowShellMenuItems = shellMenuItems.Except(mainShellMenuItems).ToList();

			var overflowItems = ItemModelListToContextFlyoutHelper.GetMenuFlyoutItemsFromModel(overflowShellMenuItems);
			var mainItems = ItemModelListToContextFlyoutHelper.GetAppBarButtonsFromModelIgnorePrimary(mainShellMenuItems);

			var openedPopups = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopups(App.Window);
			var secondaryMenu = openedPopups.FirstOrDefault(popup => popup.Name == "OverflowPopup");
			var itemsControl = secondaryMenu?.Child.FindDescendant<ItemsControl>();
			if (itemsControl is not null && secondaryMenu is not null)
			{
				contextMenuFlyout.SetValue(ContextMenuExtensions.ItemsControlProperty, itemsControl);

				var ttv = secondaryMenu.TransformToVisual(App.Window.Content);
				var cMenuPos = ttv.TransformPoint(new Point(0, 0));
				var requiredHeight = contextMenuFlyout.SecondaryCommands.Concat(mainItems).Where(x => x is not AppBarSeparator).Count() * Constants.UI.ContextMenuSecondaryItemsHeight;
				var availableHeight = App.Window.Bounds.Height - cMenuPos.Y - Constants.UI.ContextMenuPrimaryItemsHeight;
				if (requiredHeight > availableHeight)
					itemsControl.MaxHeight = Math.Min(Constants.UI.ContextMenuMaxHeight, Math.Max(itemsControl.ActualHeight, Math.Min(availableHeight, requiredHeight))); // Set menu max height to current height (avoids menu repositioning)

				mainItems.OfType<FrameworkElement>().ForEach(x => x.MaxWidth = itemsControl.ActualWidth - Constants.UI.ContextMenuLabelMargin); // Set items max width to current menu width (#5555)
			}

			var overflowItem = contextMenuFlyout.SecondaryCommands.FirstOrDefault(x => x is AppBarButton appBarButton && (appBarButton.Tag as string) == "ItemOverflow") as AppBarButton;
			if (overflowItem is not null)
			{
				var overflowItemFlyout = overflowItem.Flyout as MenuFlyout;
				if (overflowItemFlyout is not null)
				{
					if (overflowItemFlyout.Items.Count > 0)
						overflowItemFlyout.Items.Insert(0, new MenuFlyoutSeparator());

					var index = contextMenuFlyout.SecondaryCommands.Count - 2;
					foreach (var i in mainItems)
					{
						index++;
						contextMenuFlyout.SecondaryCommands.Insert(index, i);
					}

					index = 0;
					foreach (var i in overflowItems)
					{
						overflowItemFlyout.Items.Insert(index, i);
						index++;
					}

					if (overflowItemFlyout.Items.Count > 0)
					{
						(contextMenuFlyout.SecondaryCommands.First(x => x is FrameworkElement fe && fe.Tag as string == "OverflowSeparator") as AppBarSeparator)!.Visibility = Visibility.Visible;
						overflowItem.Visibility = Visibility.Visible;
					}
				}
			}
			else
			{
				mainItems.ForEach(x => contextMenuFlyout.SecondaryCommands.Add(x));
			}

			// add items to openwith dropdown
			var openWithOverflow = contextMenuFlyout.SecondaryCommands.FirstOrDefault(x => x is AppBarButton abb && (abb.Tag as string) == "OpenWithOverflow") as AppBarButton;
			var openWith = contextMenuFlyout.SecondaryCommands.FirstOrDefault(x => x is AppBarButton abb && (abb.Tag as string) == "OpenWith") as AppBarButton;
			if (openWithSubItems is not null && openWithOverflow is not null && openWith is not null)
			{
				var flyout = (MenuFlyout)openWithOverflow.Flyout;

				flyout.Items.Clear();

				foreach (var item in openWithSubItems)
				{
					flyout.Items.Add(item);
				}

				openWithOverflow.Flyout = flyout;
				openWith.Visibility = Visibility.Collapsed;
				openWithOverflow.Visibility = Visibility.Visible;
			}

			if (itemsControl is not null)
			{
				itemsControl.Items.OfType<FrameworkElement>().ForEach(item =>
				{
					if (item.FindDescendant("OverflowTextLabel") is TextBlock label) // Enable CharacterEllipsis text trimming for menu items
						label.TextTrimming = TextTrimming.CharacterEllipsis;
					if ((item as AppBarButton)?.Flyout as MenuFlyout is MenuFlyout flyout) // Close main menu when clicking on subitems (#5508)
					{
						Action<IList<MenuFlyoutItemBase>> clickAction = null!;
						clickAction = (items) =>
						{
							items.OfType<MenuFlyoutItem>().ForEach(i =>
							{
								i.Click += new RoutedEventHandler((s, e) => contextMenuFlyout.Hide());
							});
							items.OfType<MenuFlyoutSubItem>().ForEach(i =>
							{
								clickAction(i.Items);
							});
						};
						clickAction(flyout.Items);
					}
				});
			}
		}

		protected virtual void Page_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
		{
			if (ParentShellPageInstance!.IsCurrentInstance)
			{
				char letter = args.Character;
				JumpString += letter.ToString().ToLowerInvariant();
			}
		}

		protected void FileList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
		{
			SelectedItems!.AddRange(e.Items.OfType<ListedItem>());

			try
			{
				var itemList = e.Items.OfType<ListedItem>().Select(x => new VA.ShellItem(x.ItemPath)).ToArray();
				var iddo = itemList[0].Parent.GetChildrenUIObjects<IDataObject>(HWND.NULL, itemList);
				itemList.ForEach(x => x.Dispose());
				var wfdo = new System.Windows.Forms.DataObject(iddo);
				var formats = wfdo.GetFormats(false);
				foreach (var format in formats)
				{
					var clipFrmtId = (uint)System.Windows.Forms.DataFormats.GetFormat(format).Id;
					if (iddo.TryGetData<byte[]>(clipFrmtId, out var data))
					{
						var mem = new MemoryStream(data).AsRandomAccessStream();
						e.Data.SetData(format, mem);
					}
				}
			}
			catch (Exception)
			{
				e.Cancel = true;
			}
		}

		private ListedItem? dragOverItem = null;

		private void Item_DragLeave(object sender, DragEventArgs e)
		{
			var item = GetItemFromElement(sender);
			if (item == dragOverItem)
				dragOverItem = null; // Reset dragged over item
		}

		protected async void Item_DragOver(object sender, DragEventArgs e)
		{
			var item = GetItemFromElement(sender);
			if (item is null)
				return;

			DragOperationDeferral? deferral = null;
			try
			{
				deferral = e.GetDeferral();

				if (dragOverItem != item)
				{
					dragOverItem = item;
					dragOverTimer.Stop();
					dragOverTimer.Debounce(() =>
					{
						if (dragOverItem is not null && !dragOverItem.IsExecutable)
						{
							dragOverTimer.Stop();
							ItemManipulationModel.SetSelectedItem(dragOverItem);
							dragOverItem = null;
							_ = NavigationHelpers.OpenSelectedItems(ParentShellPageInstance!, false);
						}
					}, TimeSpan.FromMilliseconds(1000), false);
				}

				if (FilesystemHelpers.HasDraggedStorageItems(e.DataView))
				{
					e.Handled = true;

					var handledByFtp = await FilesystemHelpers.CheckDragNeedsFulltrust(e.DataView);
					var draggedItems = await FilesystemHelpers.GetDraggedStorageItems(e.DataView);

					if (draggedItems.Any(draggedItem => draggedItem.Path == item.ItemPath))
					{
						e.AcceptedOperation = DataPackageOperation.None;
					}
					else if (handledByFtp)
					{
						e.DragUIOverride.IsCaptionVisible = true;
						e.DragUIOverride.Caption = string.Format("CopyToFolderCaptionText".GetLocalizedResource(), item.Name);
						e.AcceptedOperation = DataPackageOperation.Copy;
					}
					else if (!draggedItems.Any())
					{
						e.AcceptedOperation = DataPackageOperation.None;
					}
					else
					{
						e.DragUIOverride.IsCaptionVisible = true;
						if (item.IsExecutable)
						{
							e.DragUIOverride.Caption = $"{"OpenItemsWithCaptionText".GetLocalizedResource()} {item.Name}";
							e.AcceptedOperation = DataPackageOperation.Link;
						} // Items from the same drive as this folder are dragged into this folder, so we move the items instead of copy
						else if (e.Modifiers.HasFlag(DragDropModifiers.Alt) || e.Modifiers.HasFlag(DragDropModifiers.Control | DragDropModifiers.Shift))
						{
							e.DragUIOverride.Caption = string.Format("LinkToFolderCaptionText".GetLocalizedResource(), item.Name);
							e.AcceptedOperation = DataPackageOperation.Link;
						}
						else if (e.Modifiers.HasFlag(DragDropModifiers.Control))
						{
							e.DragUIOverride.Caption = string.Format("CopyToFolderCaptionText".GetLocalizedResource(), item.Name);
							e.AcceptedOperation = DataPackageOperation.Copy;
						}
						else if (e.Modifiers.HasFlag(DragDropModifiers.Shift))
						{
							e.DragUIOverride.Caption = string.Format("MoveToFolderCaptionText".GetLocalizedResource(), item.Name);
							e.AcceptedOperation = DataPackageOperation.Move;
						}
						else if (draggedItems.Any(x => x.Item is ZipStorageFile || x.Item is ZipStorageFolder)
							|| ZipStorageFolder.IsZipPath(item.ItemPath))
						{
							e.DragUIOverride.Caption = string.Format("CopyToFolderCaptionText".GetLocalizedResource(), item.Name);
							e.AcceptedOperation = DataPackageOperation.Copy;
						}
						else if (draggedItems.AreItemsInSameDrive(item.ItemPath))
						{
							e.DragUIOverride.Caption = string.Format("MoveToFolderCaptionText".GetLocalizedResource(), item.Name);
							e.AcceptedOperation = DataPackageOperation.Move;
						}
						else
						{
							e.DragUIOverride.Caption = string.Format("CopyToFolderCaptionText".GetLocalizedResource(), item.Name);
							e.AcceptedOperation = DataPackageOperation.Copy;
						}
					}
				}
			}
			finally
			{
				deferral?.Complete();
			}
		}

		protected async void Item_Drop(object sender, DragEventArgs e)
		{
			var deferral = e.GetDeferral();

			e.Handled = true;
			dragOverItem = null; // Reset dragged over item

			var item = GetItemFromElement(sender);
			if (item is not null)
				await ParentShellPageInstance!.FilesystemHelpers.PerformOperationTypeAsync(e.AcceptedOperation, e.DataView, (item as ShortcutItem)?.TargetPath ?? item.ItemPath, false, true, item.IsExecutable);
			deferral.Complete();
		}

		protected void FileList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
		{
			RefreshContainer(args.ItemContainer, args.InRecycleQueue);
			RefreshItem(args.ItemContainer, args.Item, args.InRecycleQueue, args);
		}

		private void RefreshContainer(SelectorItem container, bool inRecycleQueue)
		{
			container.PointerPressed -= FileListItem_PointerPressed;
			container.PointerEntered -= FileListItem_PointerEntered;
			container.PointerExited -= FileListItem_PointerExited;
			container.RightTapped -= FileListItem_RightTapped;

			if (inRecycleQueue)
			{
				UninitializeDrag(container);
			}
			else
			{
				container.PointerPressed += FileListItem_PointerPressed;
				container.RightTapped += FileListItem_RightTapped;
				if (UserSettingsService.PreferencesSettingsService.SelectFilesOnHover)
				{
					container.PointerEntered += FileListItem_PointerEntered;
					container.PointerExited += FileListItem_PointerExited;
				}
			}
		}

		private void RefreshItem(SelectorItem container, object item, bool inRecycleQueue, ContainerContentChangingEventArgs args)
		{
			if (item is not ListedItem listedItem)
				return;

			if (inRecycleQueue)
			{
				ParentShellPageInstance!.FilesystemViewModel.CancelExtendedPropertiesLoadingForItem(listedItem);
			}
			else
			{
				InitializeDrag(container, listedItem);

				if (!listedItem.ItemPropertiesInitialized)
				{
					uint callbackPhase = 3;
					args.RegisterUpdateCallback(callbackPhase, async (s, c) =>
					{
						await ParentShellPageInstance!.FilesystemViewModel.LoadExtendedItemProperties(listedItem, IconSize);
					});
				}
			}
		}

		protected static void FileListItem_PointerPressed(object sender, PointerRoutedEventArgs e)
		{
			if (sender is not SelectorItem selectorItem)
				return;

			if (selectorItem.IsSelected && e.KeyModifiers == VirtualKeyModifiers.Control)
			{
				selectorItem.IsSelected = false;
				// Prevent issues arising caused by the default handlers attempting to select the item that has just been deselected by ctrl + click
				e.Handled = true;
			}
			else if (!selectorItem.IsSelected && e.GetCurrentPoint(selectorItem).Properties.IsLeftButtonPressed)
			{
				selectorItem.IsSelected = true;
			}
		}

		private ListedItem? hoveredItem = null;

		protected internal void FileListItem_PointerEntered(object sender, PointerRoutedEventArgs e)
		{
			if (!UserSettingsService.PreferencesSettingsService.SelectFilesOnHover)
				return;

			var hovered = GetItemFromElement(sender);
			if (hovered != hoveredItem)
			{
				hoveredItem = hovered;
				hoverTimer.Stop();
				hoverTimer.Debounce(() =>
				{
					if (hoveredItem is not null)
					{
						hoverTimer.Stop();
						if (e.KeyModifiers == VirtualKeyModifiers.Control &&
							selectedItems is not null)
						{
							ItemManipulationModel.AddSelectedItem(hoveredItem);
						}
						else if (e.KeyModifiers == VirtualKeyModifiers.Shift &&
							selectedItems is not null &&
							selectedItems.Any())
						{
							var last = selectedItems.Last();
							byte found = 0;
							for (int i = 0; i < ItemsControl.Items.Count && found != 2; i++)
							{
								if (ItemsControl.Items[i] == last || ItemsControl.Items[i] == hoveredItem)
									found++;
								if (found != 0 && !selectedItems.Contains(ItemsControl.Items[i]))
									ItemManipulationModel.AddSelectedItem((ListedItem)ItemsControl.Items[i]);
							}
						}
						else
						{
							ItemManipulationModel.SetSelectedItem(hoveredItem);
						}
						hoveredItem = null;
					}
				}, TimeSpan.FromMilliseconds(600), false);
			}
		}

		protected internal void FileListItem_PointerExited(object sender, PointerRoutedEventArgs e)
		{
			if (!UserSettingsService.PreferencesSettingsService.SelectFilesOnHover)
				return;

			hoverTimer.Stop();
			hoveredItem = null;
		}

		protected void FileListItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
		{
			var rightClickedItem = GetItemFromElement(sender);

			if (rightClickedItem is not null && !((SelectorItem)sender).IsSelected)
				ItemManipulationModel.SetSelectedItem(rightClickedItem);
		}

		private readonly RecycleBinHelpers recycleBinHelpers = new();

		protected void InitializeDrag(UIElement containter, ListedItem item)
		{
			if (item is null)
				return;

			UninitializeDrag(containter);
			if ((item.PrimaryItemAttribute == StorageItemTypes.Folder && !recycleBinHelpers.IsPathUnderRecycleBin(item.ItemPath)) || item.IsExecutable)
			{
				containter.AllowDrop = true;
				containter.DragOver += Item_DragOver;
				containter.DragLeave += Item_DragLeave;
				containter.Drop += Item_Drop;
			}
		}

		protected void UninitializeDrag(UIElement element)
		{
			element.AllowDrop = false;
			element.DragOver -= Item_DragOver;
			element.DragLeave -= Item_DragLeave;
			element.Drop -= Item_Drop;
		}

		// VirtualKey doesn't support / accept plus and minus by default.
		public readonly VirtualKey PlusKey = (VirtualKey)187;

		public readonly VirtualKey MinusKey = (VirtualKey)189;

		public virtual void Dispose()
		{
			PaneViewModel?.Dispose();
			UnhookBaseEvents();
		}

		protected void ItemsLayout_DragOver(object sender, DragEventArgs e)
		{
			CommandsViewModel?.DragOverCommand?.Execute(e);
		}

		protected void ItemsLayout_Drop(object sender, DragEventArgs e)
		{
			CommandsViewModel?.DropCommand?.Execute(e);
		}

		private void UpdateCollectionViewSource()
		{
			if (ParentShellPageInstance is null)
				return;
			if (ParentShellPageInstance.FilesystemViewModel.FilesAndFolders.IsGrouped)
			{
				CollectionViewSource = new CollectionViewSource()
				{
					IsSourceGrouped = true,
					Source = ParentShellPageInstance.FilesystemViewModel.FilesAndFolders.GroupedCollection
				};
			}
			else
			{
				CollectionViewSource = new CollectionViewSource()
				{
					IsSourceGrouped = false,
					Source = ParentShellPageInstance.FilesystemViewModel.FilesAndFolders
				};
			}
		}

		protected void SemanticZoom_ViewChangeStarted(object sender, SemanticZoomViewChangedEventArgs e)
		{
			if (!e.IsSourceZoomedInView)
			{
				// According to the docs this isn't necessary, but it would crash otherwise
				var destination = e.DestinationItem.Item as GroupedCollection<ListedItem>;
				e.DestinationItem.Item = destination?.FirstOrDefault();
			}
		}

		protected void StackPanel_PointerEntered(object sender, PointerRoutedEventArgs e)
		{
			var element = (sender as UIElement)?.FindAscendant<ListViewBaseHeaderItem>();
			if (element is not null)
				VisualStateManager.GoToState(element, "PointerOver", true);
		}

		protected void StackPanel_PointerCanceled(object sender, PointerRoutedEventArgs e)
		{
			var element = (sender as UIElement)?.FindAscendant<ListViewBaseHeaderItem>();
			if (element is not null)
				VisualStateManager.GoToState(element, "Normal", true);
		}

		protected void RootPanel_PointerPressed(object sender, PointerRoutedEventArgs e)
		{
			var element = (sender as UIElement)?.FindAscendant<ListViewBaseHeaderItem>();
			if (element is not null)
				VisualStateManager.GoToState(element, "Pressed", true);
		}

		private void ItemManipulationModel_RefreshItemsOpacityInvoked(object? sender, EventArgs e)
		{
			foreach (ListedItem listedItem in GetAllItems())
			{
				if (listedItem.IsHiddenItem)
					listedItem.Opacity = Constants.UI.DimItemOpacity;
				else
					listedItem.Opacity = 1;
			}
		}

		private void View_VectorChanged(IObservableVector<object> sender, IVectorChangedEventArgs @event)
		{
			if (ParentShellPageInstance is not null)
				ParentShellPageInstance.ToolbarViewModel.HasItem = CollectionViewSource.View.Any();
		}

		virtual public void StartRenameItem() { }

		private ListedItem? preRenamingItem = null;

		public void CheckRenameDoubleClick(object clickedItem)
		{
			if (clickedItem is ListedItem item)
			{
				if (item == preRenamingItem)
				{
					tapDebounceTimer.Debounce(() =>
					{
						if (item == preRenamingItem)
						{
							StartRenameItem();
							tapDebounceTimer.Stop();
						}
					}, TimeSpan.FromMilliseconds(500));
				}
				else
				{
					tapDebounceTimer.Stop();
					preRenamingItem = item;
				}
			}
			else
			{
				ResetRenameDoubleClick();
			}
		}

		public void ResetRenameDoubleClick()
		{
			preRenamingItem = null;
			tapDebounceTimer.Stop();
		}

		protected async void ValidateItemNameInputText(TextBox textBox, TextBoxBeforeTextChangingEventArgs args, Action<bool> showError)
		{
			if (FilesystemHelpers.ContainsRestrictedCharacters(args.NewText))
			{
				args.Cancel = true;
				await DispatcherQueue.EnqueueAsync(() =>
				{
					var oldSelection = textBox.SelectionStart + textBox.SelectionLength;
					var oldText = textBox.Text;
					textBox.Text = FilesystemHelpers.FilterRestrictedCharacters(args.NewText);
					textBox.SelectionStart = oldSelection + textBox.Text.Length - oldText.Length;
					showError?.Invoke(true);
				});
			}
			else
			{
				showError?.Invoke(false);
			}
		}
	}

	public class ContextMenuExtensions : DependencyObject
	{
		public static ItemsControl GetItemsControl(DependencyObject obj)
		{
			return (ItemsControl)obj.GetValue(ItemsControlProperty);
		}

		public static void SetItemsControl(DependencyObject obj, ItemsControl value)
		{
			obj.SetValue(ItemsControlProperty, value);
		}

		public static readonly DependencyProperty ItemsControlProperty =
			DependencyProperty.RegisterAttached("ItemsControl", typeof(ItemsControl), typeof(ContextMenuExtensions), new PropertyMetadata(null));
	}
}

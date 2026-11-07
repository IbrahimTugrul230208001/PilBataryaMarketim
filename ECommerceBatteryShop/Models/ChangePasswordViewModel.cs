using System.ComponentModel.DataAnnotations;

namespace ECommerceBatteryShop.Models;

public class ChangePasswordViewModel
{
 [Required(ErrorMessage = "Mevcut Şifre gereklidir.")]
 [DataType(DataType.Password)]
 public string CurrentPassword { get; set; } = string.Empty;

 [Required(ErrorMessage = "Yeni Şifre gereklidir.")]
 [MinLength(6, ErrorMessage = "Şifre en az 6 karakter olmalıdır.")]
 [DataType(DataType.Password)]
 public string NewPassword { get; set; } = string.Empty;

 [Required(ErrorMessage = "Şifre tekrarı gereklidir.")]
 [DataType(DataType.Password)]
 [Compare("NewPassword", ErrorMessage = "Şifreler eşleşmiyor.")]
 public string ConfirmPassword { get; set; } = string.Empty;
}
